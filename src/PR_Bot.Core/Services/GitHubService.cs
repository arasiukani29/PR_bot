namespace PR_Bot.Core.Services;

using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using PR_Bot.Core.Configuration;
using PR_Bot.Core.Models;

/// <summary>
/// GitHub service implementation using Octokit.
/// </summary>
public sealed class GitHubService : IGitHubService
{
    public const string BotCommentMarker = "<!-- universal-pr-bot-review -->";

    private readonly IGitHubClient _client;
    private readonly PrBotOptions _options;
    private readonly ILogger<GitHubService> _logger;

    public GitHubService(
        IGitHubClient client,
        IOptions<PrBotOptions>? options = null,
        ILogger<GitHubService>? logger = null)
    {
        _client = client;
        _options = options?.Value ?? new PrBotOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubService>.Instance;
    }

    public async Task<PullRequestContext> FetchChangedFilesAsync(
        PullRequestContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Fetching changed files for PR #{PrNumber} in {Owner}/{Repo}...",
            context.PullRequestNumber, context.Owner, context.Repo);

        if (string.IsNullOrWhiteSpace(context.Description))
        {
            try
            {
                var pr = await _client.PullRequest.Get(context.Owner, context.Repo, context.PullRequestNumber);
                context.Description = pr.Body;
                if (!string.IsNullOrWhiteSpace(context.Description))
                {
                    _logger.LogInformation("Fetched PR description ({Length} chars) for PR #{PrNumber}.", context.Description.Length, context.PullRequestNumber);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not fetch PR description for PR #{PrNumber}.", context.PullRequestNumber);
            }
        }

        var files = await _client.PullRequest.Files(
            context.Owner,
            context.Repo,
            context.PullRequestNumber);

        var changedFiles = new List<PullRequestFileChange>();

        foreach (var file in files)
        {
            if (IsIgnored(file.FileName))
            {
                _logger.LogDebug("Skipping ignored file: {FileName}", file.FileName);
                continue;
            }

            string? fullContent = null;
            if (!file.Status.Equals("removed", StringComparison.OrdinalIgnoreCase))
            {
                // Attempt 1: Read directly from local disk (available if runner checked out the repository)
                if (File.Exists(file.FileName))
                {
                    try
                    {
                        fullContent = await File.ReadAllTextAsync(file.FileName, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not read local file {FileName} for full context.", file.FileName);
                    }
                }

                // Attempt 2: Fallback to Octokit GitHub API if not found locally
                if (fullContent == null && !string.IsNullOrWhiteSpace(context.HeadSha) && !context.HeadSha.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var bytes = await _client.Repository.Content.GetRawContentByRef(context.Owner, context.Repo, file.FileName, context.HeadSha);
                        fullContent = Encoding.UTF8.GetString(bytes);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not fetch remote raw content from GitHub for {FileName}.", file.FileName);
                    }
                }
            }

            changedFiles.Add(new PullRequestFileChange
            {
                FileName = file.FileName,
                Status = file.Status,
                Additions = file.Additions,
                Deletions = file.Deletions,
                Changes = file.Changes,
                Patch = file.Patch,
                FullContent = fullContent
            });
        }

        context.ChangedFiles = changedFiles;

        _logger.LogInformation(
            "Fetched {Count} eligible files (out of {TotalFiles} total) for PR #{PrNumber}",
            changedFiles.Count, files.Count, context.PullRequestNumber);

        return context;
    }

    public async Task PostReviewAsync(
        PullRequestContext context,
        ReviewResult result,
        CancellationToken cancellationToken = default)
    {
        var summaryMarkdown = BuildReviewMarkdown(context, result);

        // Submit the complete review (overall summary + inline comments) in a single GitHub Review
        await PostReviewBatchAsync(context, result, summaryMarkdown, cancellationToken);
    }

    private async Task PostReviewBatchAsync(
        PullRequestContext context,
        ReviewResult result,
        string summaryMarkdown,
        CancellationToken cancellationToken)
    {
        // Fetch existing review comments on the PR to prevent duplicate comments across commits
        IReadOnlyList<PullRequestReviewComment> existingComments = [];
        try
        {
            existingComments = await _client.PullRequest.ReviewComment.GetAll(
                context.Owner,
                context.Repo,
                context.PullRequestNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch existing review comments for deduplication.");
        }

        // Collect new comments that have valid diff positions and haven't already been reported
        var inlineComments = new List<DraftPullRequestReviewComment>();

        foreach (var comment in result.Comments)
        {
            if (comment.DiffPosition.HasValue && comment.DiffPosition.Value > 0)
            {
                var body = $"{comment.Body}\n\n<sub>Reported by {comment.AnalyzerName}</sub>";

                // Check if this exact finding has already been posted to avoid duplicate spam across runs
                var snippet = comment.Body.Length > 80 ? comment.Body[..80] : comment.Body;
                bool isDuplicate = existingComments.Any(e =>
                    e.Path.Equals(comment.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    (e.Body.Contains(comment.Body) || e.Body.Contains(snippet)));

                if (!isDuplicate)
                {
                    inlineComments.Add(new DraftPullRequestReviewComment(body, comment.FilePath, comment.DiffPosition.Value));
                }
                else
                {
                    _logger.LogInformation("Skipping already reported comment on {File} (Line {Line})", comment.FilePath, comment.Line);
                }
            }
        }

        _logger.LogInformation(
            "Submitting review to PR #{PrNumber} with {Count} inline comment(s)...",
            context.PullRequestNumber, inlineComments.Count);

        try
        {
            var review = new PullRequestReviewCreate
            {
                CommitId = context.HeadSha,
                Event = PullRequestReviewEvent.Comment,
                Body = summaryMarkdown, // Posts the overall summary table in the review
                Comments = inlineComments.Count > 0 ? inlineComments : null
            };

            await _client.PullRequest.Review.Create(
                context.Owner,
                context.Repo,
                context.PullRequestNumber,
                review);

            _logger.LogInformation("Successfully submitted PR review.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to submit review batch. Falling back to summary comment.");
            await UpsertSummaryCommentAsync(context, summaryMarkdown, cancellationToken);
        }
    }

    private async Task UpsertSummaryCommentAsync(
        PullRequestContext context,
        string summaryMarkdown,
        CancellationToken cancellationToken)
    {
        try
        {
            // Check existing comments for our unique bot marker
            var existingComments = await _client.Issue.Comment.GetAllForIssue(
                context.Owner,
                context.Repo,
                context.PullRequestNumber);

            var previousBotComment = existingComments.FirstOrDefault(c =>
                c.Body != null && c.Body.Contains(BotCommentMarker));

            if (previousBotComment != null)
            {
                _logger.LogInformation("Updating existing PR review comment ID {Id}...", previousBotComment.Id);
                await _client.Issue.Comment.Update(
                    context.Owner,
                    context.Repo,
                    previousBotComment.Id,
                    summaryMarkdown);
                _logger.LogInformation("Updated existing review comment.");
            }
            else
            {
                _logger.LogInformation("Posting new PR review summary comment...");
                await _client.Issue.Comment.Create(
                    context.Owner,
                    context.Repo,
                    context.PullRequestNumber,
                    summaryMarkdown);
                _logger.LogInformation("Created new review summary comment.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post or update summary comment on PR #{PrNumber}", context.PullRequestNumber);
        }
    }

    private bool IsIgnored(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        foreach (var pattern in _options.IgnoredFilePatterns)
        {
            var p = pattern.ToLowerInvariant();
            if (p.StartsWith("*.") && lower.EndsWith(p[1..]))
            {
                return true;
            }
            if (lower.Equals(p, StringComparison.OrdinalIgnoreCase) || lower.EndsWith("/" + p))
            {
                return true;
            }
        }
        return false;
    }

    public static string BuildReviewMarkdown(PullRequestContext context, ReviewResult result)
    {
        var criticalCount = result.CriticalCount;
        var highCount = result.HighCount;
        var mediumCount = result.MediumCount;
        var lowCount = result.LowCount;
        var totalCount = criticalCount + highCount + mediumCount + lowCount;

        var sb = new StringBuilder();

        // 1. Hidden marker to identify bot comments for upsert
        sb.AppendLine(BotCommentMarker);

        // 2. Header banner
        sb.AppendLine("## 🤖 Universal PR Review Bot — Review Dashboard");
        sb.AppendLine();
        sb.AppendLine($"> **Pull Request:** `#{context.PullRequestNumber}` — **{context.Title}**  ");
        sb.AppendLine($"> 👤 **Author:** @{context.Author} &nbsp;•&nbsp; 🌿 **Branches:** `{context.HeadBranch}` ➔ `{context.BaseBranch}` &nbsp;•&nbsp; 📁 **Files Audited:** {context.ChangedFiles.Count}");
        sb.AppendLine();

        // 3. Verdict Alert Banner (GitHub Flavored Markdown native alert)
        if (criticalCount > 0)
        {
            sb.AppendLine("> [!CAUTION]");
            sb.AppendLine($"> ### 🚨 VERDICT: CHANGES REQUIRED ({criticalCount} CRITICAL BLOCKER{(criticalCount > 1 ? "S" : "")})");
            sb.AppendLine("> Critical security or crash hazards detected. These issues must be resolved before this pull request can be merged.");
        }
        else if (highCount > 0)
        {
            sb.AppendLine("> [!WARNING]");
            sb.AppendLine($"> ### 🔴 VERDICT: CHANGES REQUESTED ({highCount} HIGH PRIORITY DEFECT{(highCount > 1 ? "S" : "")})");
            sb.AppendLine("> Major functional defects or reliability hazards detected. Please resolve the flagged issues before merge.");
        }
        else if (mediumCount > 0)
        {
            sb.AppendLine("> [!IMPORTANT]");
            sb.AppendLine($"> ### 🟡 VERDICT: REVIEW RECOMMENDED ({mediumCount} MEDIUM PRIORITY ISSUE{(mediumCount > 1 ? "S" : "")})");
            sb.AppendLine("> No critical blockers found, but edge-case validations or logic improvements are strongly recommended.");
        }
        else if (lowCount > 0)
        {
            sb.AppendLine("> [!NOTE]");
            sb.AppendLine($"> ### 🟢 VERDICT: APPROVED WITH MINOR SUGGESTIONS ({lowCount} SUGGESTION{(lowCount > 1 ? "S" : "")})");
            sb.AppendLine("> The pull request is safe to merge. Minor optional suggestions are available for code hygiene and modern idioms.");
        }
        else
        {
            sb.AppendLine("> [!TIP]");
            sb.AppendLine("> ### ✨ VERDICT: READY TO MERGE — PERFECT SCORE (100/100)");
            sb.AppendLine("> All automated security, reliability, and code quality checks have passed with zero defects. Clean PR! 🎉");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // 4. Code Quality & Health Scorecard
        sb.AppendLine("### 🩺 Code Quality & Health Scorecard");
        sb.AppendLine();
        sb.AppendLine("| Metric | Result / Status | Benchmark Visualizer |");
        sb.AppendLine("| :--- | :---: | :--- |");
        sb.AppendLine($"| **Code Health Score** | **{result.HealthScore} / 100** | `{result.HealthProgressBar}` |");
        sb.AppendLine($"| **Quality Grade** | **Grade {result.HealthGrade}** | 🟢 **A** (90-100) &nbsp; 🟡 **B** (75-89) &nbsp; 🟠 **C** (50-74) &nbsp; 🔴 **F** (<50) |");
        sb.AppendLine($"| **Defects Flagged** | **{totalCount} Total Issues** | 🚨 **{criticalCount}** Critical &nbsp;•&nbsp; 🔴 **{highCount}** High &nbsp;•&nbsp; 🟡 **{mediumCount}** Medium &nbsp;•&nbsp; 🟢 **{lowCount}** Low |");
        sb.AppendLine($"| **Quality Gate** | **{(criticalCount == 0 && highCount == 0 ? "PASSED ✅" : "FAILED ❌")}** | {(criticalCount == 0 && highCount == 0 ? "Meets safety criteria for team review" : "Must resolve Critical & High issues to pass")} |");
        sb.AppendLine();

        // 5. Defect Priority Breakdown Table
        sb.AppendLine("#### 📊 Defect Classification & Merge Gate");
        sb.AppendLine();
        sb.AppendLine("| Status | Severity Level | Impact & Merge Policy | Issues Found |");
        sb.AppendLine("|:------:|:---------------|:----------------------|:------------:|");
        sb.AppendLine($"| {(criticalCount > 0 ? "🚨" : "✅")} | 🚨 **Critical** | Exploitable vulnerability, secret leak, or crash | **{criticalCount}** |");
        sb.AppendLine($"| {(highCount > 0 ? "🔴" : "✅")} | 🔴 **High Priority** | Major logic defect, race condition, data loss risk | **{highCount}** |");
        sb.AppendLine($"| {(mediumCount > 0 ? "🟡" : "✅")} | 🟡 **Medium Priority** | Missing input validation, boundary condition, error handling | **{mediumCount}** |");
        sb.AppendLine($"| {(lowCount > 0 ? "🟢" : "✅")} | 🟢 **Low Priority** | Code hygiene, modern idiom, optional suggestion | **{lowCount}** |");
        sb.AppendLine();

        // 6. Audited Files Matrix (if changed files are present)
        if (context.ChangedFiles.Count > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("### 📁 Audited Files Matrix");
            sb.AppendLine();
            sb.AppendLine("| Status | File | Changes | Findings |");
            sb.AppendLine("|:------:|:-----|:-------:|:---------|");

            foreach (var file in context.ChangedFiles)
            {
                var fileFindings = result.Comments
                    .Where(c => c.FilePath.Equals(file.FileName, StringComparison.OrdinalIgnoreCase) ||
                                file.FileName.EndsWith("/" + c.FilePath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var (fileStatus, findingsSummary) = GetFileStatusAndSummary(fileFindings);
                var changesStr = $"`+{file.Additions} / -{file.Deletions}`";

                sb.AppendLine($"| {fileStatus} | `{file.FileName}` | {changesStr} | {findingsSummary} |");
            }
            sb.AppendLine();
        }

        // 7. AI Executive Summary (if provided by Gemini)
        if (!string.IsNullOrWhiteSpace(result.SummaryMarkdown))
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("### 🧠 AI Executive Summary (Google Gemini)");
            sb.AppendLine();
            sb.AppendLine($"> {result.SummaryMarkdown.Replace("\n", "\n> ")}");
            sb.AppendLine();
        }

        // 8. Detailed Findings Grouped by Priority
        if (result.Comments.Count > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("### 📋 Detailed Findings & Action Items");
            sb.AppendLine();

            var priorityGroups = result.Comments
                .GroupBy(c => c.Severity)
                .OrderByDescending(g => g.Key); // Critical -> High -> Medium -> Low

            foreach (var group in priorityGroups)
            {
                var (title, icon, isOpen) = group.Key switch
                {
                    AnalyzerSeverity.Critical => ("🚨 Critical Blockers (Immediate Security/Crash Hazards)", "🚨", true),
                    AnalyzerSeverity.Error => ("🔴 High Priority Issues (Must Resolve Before Merge)", "🔴", true),
                    AnalyzerSeverity.Warning => ("🟡 Medium Priority Issues (Recommended to Fix)", "⚠️", false),
                    _ => ("🟢 Low Priority Suggestions (Improvements)", "💡", false)
                };

                var openAttr = isOpen ? " open" : "";
                sb.AppendLine($"<details{openAttr}>");
                sb.AppendLine($"<summary><b>{title} — {group.Count()} issue{(group.Count() > 1 ? "s" : "")}</b></summary>");
                sb.AppendLine();

                foreach (var comment in group)
                {
                    var titleSnippet = !string.IsNullOrWhiteSpace(comment.Title) ? $" — {comment.Title}" : "";
                    sb.AppendLine($"#### {icon} `{comment.FilePath}` (Line {comment.Line}){titleSnippet}");
                    sb.AppendLine();
                    sb.AppendLine(comment.Body);
                    sb.AppendLine();
                    sb.AppendLine("---");
                }

                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("### 🎉 All Checks Clear");
            sb.AppendLine();
            sb.AppendLine("✨ **No code quality, reliability, or security issues detected in this pull request! Clean PR.** 🎉");
            sb.AppendLine();
        }

        // 9. Actionable Next Steps Checklist
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("### 💡 Developer Next Steps");
        sb.AppendLine();
        if (totalCount > 0)
        {
            sb.AppendLine("- 🛠️ **Apply Fixes:** Open the **[Files changed]** tab above to review inline comments and apply one-click suggestions.");
            sb.AppendLine("- 🔄 **Automatic Re-verification:** Push your fix commit to this branch — the bot will re-evaluate modified files and automatically mark resolved comments as `[Outdated]`.");
        }
        else
        {
            sb.AppendLine("- ✅ **Ready for Merge:** All automated checks passed. Proceed to human peer review and merge when ready.");
        }
        sb.AppendLine();

        // 10. Footer
        sb.AppendLine("---");
        sb.AppendLine("<sub>Reviewed automatically with [Universal PR Bot](https://github.com/marketplace) • Powered by Google Gemini AI & .NET 10</sub>");

        return sb.ToString();
    }

    private static (string StatusIcon, string Summary) GetFileStatusAndSummary(List<ReviewComment> comments)
    {
        if (comments.Count == 0)
        {
            return ("✅", "Clean (0 issues)");
        }

        var crit = comments.Count(c => c.Severity == AnalyzerSeverity.Critical);
        var high = comments.Count(c => c.Severity == AnalyzerSeverity.Error);
        var med = comments.Count(c => c.Severity == AnalyzerSeverity.Warning);
        var low = comments.Count(c => c.Severity == AnalyzerSeverity.Info);

        var statusIcon = crit > 0 ? "🚨" : high > 0 ? "🔴" : med > 0 ? "🟡" : "🟢";

        var parts = new List<string>();
        if (crit > 0) parts.Add($"{crit} Critical");
        if (high > 0) parts.Add($"{high} High");
        if (med > 0) parts.Add($"{med} Medium");
        if (low > 0) parts.Add($"{low} Low");

        return (statusIcon, string.Join(", ", parts));
    }
}
