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
    public const string FindingMarkerPrefix = "<!-- pr-bot-finding:";
    public const string ResolvedMarker = "<!-- pr-bot-resolved -->";

    private static readonly System.Text.RegularExpressions.Regex FindingMarkerRegex = new(
        @"<!--\s*pr-bot-finding:file=(?<file>[^:]+):line=(?<line>\d+)\s*-->",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly IGitHubClient _client;
    private readonly PrBotOptions _options;
    private readonly IPatchResolutionExtractor _patchExtractor;
    private readonly ILogger<GitHubService> _logger;

    public GitHubService(
        IGitHubClient client,
        IOptions<PrBotOptions>? options = null,
        IPatchResolutionExtractor? patchExtractor = null,
        ILogger<GitHubService>? logger = null)
    {
        _client = client;
        _options = options?.Value ?? new PrBotOptions();
        _patchExtractor = patchExtractor ?? new PatchResolutionExtractor();
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
        // 1. Reconcile any previously reported comments that have been resolved
        await ReconcileResolvedCommentsAsync(context, result, cancellationToken);

        // 2. Build the full summary markdown dashboard
        var summaryMarkdown = BuildReviewMarkdown(context, result);

        // 3. Upsert the sticky overall summary dashboard comment in-place.
        // This edits the existing comment if one exists, or creates it once.
        // This prevents duplicate summary blocks in the PR conversation timeline!
        await UpsertSummaryCommentAsync(context, summaryMarkdown, cancellationToken);

        // 4. Submit new inline comments and/or update the review status event (APPROVE/REQUEST_CHANGES)
        await PostReviewBatchAsync(context, result, cancellationToken);
    }

    public async Task ReconcileResolvedCommentsAsync(
        PullRequestContext context,
        ReviewResult result,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Reconciling previously posted bot comments for PR #{PrNumber}...", context.PullRequestNumber);

        IReadOnlyList<PullRequestReviewComment> existingComments;
        try
        {
            existingComments = await _client.PullRequest.ReviewComment.GetAll(
                context.Owner,
                context.Repo,
                context.PullRequestNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch review comments for resolution reconciliation.");
            return;
        }

        if (existingComments.Count == 0)
        {
            return;
        }

        // Identify comments posted by the bot
        var botComments = existingComments.Where(c =>
            c.Body != null &&
            (c.Body.Contains(FindingMarkerPrefix) ||
             c.Body.Contains(ResolvedMarker) ||
             c.Body.Contains("[RESOLVED]") ||
             c.Body.Contains("Reported by ") ||
             (c.User?.Login?.Contains("bot", StringComparison.OrdinalIgnoreCase) == true))).ToList();

        foreach (var comment in botComments)
        {
            // Skip comments that are already marked resolved
            if (comment.Body.Contains(ResolvedMarker) || comment.Body.Contains("[RESOLVED]"))
            {
                continue;
            }

            // Find if this comment's file was modified in this PR
            var changedFile = context.ChangedFiles.FirstOrDefault(f =>
                f.FileName.Equals(comment.Path, StringComparison.OrdinalIgnoreCase));

            if (changedFile == null)
            {
                // File wasn't modified in this PR context
                continue;
            }

            int commentLine = 0;
            var markerMatch = FindingMarkerRegex.Match(comment.Body);
            if (markerMatch.Success && int.TryParse(markerMatch.Groups["line"].Value, out int parsedLine))
            {
                commentLine = parsedLine;
            }
            else if (!string.IsNullOrWhiteSpace(comment.DiffHunk))
            {
                var hunkMatch = System.Text.RegularExpressions.Regex.Match(comment.DiffHunk, @"\+(\d+)");
                if (hunkMatch.Success && int.TryParse(hunkMatch.Groups[1].Value, out int hunkLine))
                {
                    commentLine = hunkLine;
                }
            }

            // Check if there is still an active defect reported on this file near this line
            bool isStillDefective = result.Comments.Any(c =>
                c.FilePath.Equals(comment.Path, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(c.Line - commentLine) <= 3);

            if (!isStillDefective)
            {
                _logger.LogInformation(
                    "Comment #{CommentId} on {Path}:{Line} detected as RESOLVED. Updating comment in-place...",
                    comment.Id, comment.Path, commentLine);

                // Extract the diff of code added/modified that resolved this issue
                var diffSnippet = _patchExtractor.ExtractResolutionDiff(changedFile.Patch, commentLine);

                string codeSection = !string.IsNullOrWhiteSpace(diffSnippet)
                    ? $"""
                      **Code changes applied to resolve:**
                      ```diff
                      {diffSnippet}
                      ```
                      """
                    : "*Code changes detected and verified on this file.*";

                var commitSnippet = !string.IsNullOrWhiteSpace(context.HeadSha) && context.HeadSha.Length >= 7
                    ? context.HeadSha[..7]
                    : context.HeadSha ?? "latest commit";

                string updatedBody = $"""
                    ### ✅ [RESOLVED] Issue Fixed in `{commitSnippet}`

                    {codeSection}

                    ---
                    <details>
                    <summary>Original Finding</summary>

                    {comment.Body}
                    </details>

                    {ResolvedMarker}
                    """;

                try
                {
                    await _client.PullRequest.ReviewComment.Edit(
                        context.Owner,
                        context.Repo,
                        comment.Id,
                        new PullRequestReviewCommentEdit(updatedBody));

                    _logger.LogInformation("Successfully updated comment #{CommentId} to [RESOLVED].", comment.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to edit review comment #{CommentId} on {Path}.", comment.Id, comment.Path);
                }
            }
        }
    }

    private async Task PostReviewBatchAsync(
        PullRequestContext context,
        ReviewResult result,
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

        // Collect new comments that have valid lines and haven't already been reported
        var inlineComments = new List<ReviewCommentSubmissionPayload>();

        foreach (var comment in result.Comments)
        {
            if (comment.Line > 0)
            {
                var marker = $"{FindingMarkerPrefix}file={comment.FilePath}:line={comment.Line} -->";
                var body = $"{comment.Body}\n\n<sub>Reported by {comment.AnalyzerName}</sub>\n{marker}";

                // Check if this exact finding has already been posted to avoid duplicate spam across runs
                var snippet = comment.Body.Length > 80 ? comment.Body[..80] : comment.Body;
                bool isDuplicate = existingComments.Any(e =>
                    e.Path.Equals(comment.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    (e.Body.Contains(marker) || e.Body.Contains(comment.Body) || e.Body.Contains(snippet)));

                if (!isDuplicate)
                {
                    int? multilineStart = (comment.StartLine.HasValue && comment.StartLine.Value < comment.Line) ? comment.StartLine.Value : null;
                    inlineComments.Add(new ReviewCommentSubmissionPayload
                    {
                        Path = comment.FilePath,
                        Body = body,
                        Line = comment.Line,
                        StartLine = multilineStart,
                        Side = "RIGHT",
                        StartSide = multilineStart.HasValue ? "RIGHT" : null
                    });
                }
                else
                {
                    _logger.LogInformation("Skipping already reported comment on {File} (Line {Line})", comment.FilePath, comment.Line);
                }
            }
        }

        var reviewEvent = DetermineReviewEvent(result, context.ReviewEventMode);
        var reviewEventStr = reviewEvent switch
        {
            PullRequestReviewEvent.Approve => "APPROVE",
            PullRequestReviewEvent.RequestChanges => "REQUEST_CHANGES",
            _ => "COMMENT"
        };

        // Check previous reviews submitted by the bot to avoid redundant reviews if state is unchanged and no new comments
        bool shouldSubmitReview = inlineComments.Count > 0;
        try
        {
            var existingReviews = await _client.PullRequest.Review.GetAll(
                context.Owner,
                context.Repo,
                context.PullRequestNumber);

            var latestBotReview = existingReviews
                .Where(r => r.User != null && (r.User.Login?.Contains("bot", StringComparison.OrdinalIgnoreCase) == true || r.User.Login == "github-actions[bot]"))
                .OrderByDescending(r => r.SubmittedAt)
                .FirstOrDefault();

            if (latestBotReview == null)
            {
                shouldSubmitReview = true;
            }
            else
            {
                var latestStateStr = latestBotReview.State.StringValue;
                if (!string.Equals(latestStateStr, reviewEventStr, StringComparison.OrdinalIgnoreCase))
                {
                    shouldSubmitReview = true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check prior review state.");
            shouldSubmitReview = true;
        }

        if (!shouldSubmitReview)
        {
            _logger.LogInformation("Review status unchanged ({Event}) and no new inline comments. Skipping duplicate review event submission.", reviewEventStr);
            return;
        }

        string reviewSummaryNote = reviewEvent switch
        {
            PullRequestReviewEvent.Approve => "✅ **Automated Review: APPROVED** — All automated checks passed with zero defects. Full scorecard updated in the review dashboard above.",
            PullRequestReviewEvent.RequestChanges => $"🚨 **Automated Review: CHANGES REQUESTED** — {result.CriticalCount + result.HighCount} high-priority defect(s) detected. Full scorecard updated in the review dashboard above.",
            _ => "🟡 **Automated Review: COMMENT** — Advisory suggestions available. Full scorecard updated in the review dashboard above."
        };

        _logger.LogInformation(
            "Submitting review to PR #{PrNumber} with event {Event} and {Count} inline comment(s)...",
            context.PullRequestNumber, reviewEvent, inlineComments.Count);

        try
        {
            var reviewPayload = new ReviewSubmissionPayload
            {
                CommitId = context.HeadSha,
                Event = reviewEventStr,
                Body = reviewSummaryNote,
                Comments = inlineComments.Count > 0 ? inlineComments : null
            };

            var endpoint = new Uri($"repos/{context.Owner}/{context.Repo}/pulls/{context.PullRequestNumber}/reviews", UriKind.Relative);

            try
            {
                await _client.Connection.Post<PullRequestReview>(
                    endpoint,
                    reviewPayload,
                    "application/vnd.github+json",
                    "application/json",
                    (IDictionary<string, string>?)null,
                    cancellationToken);

                _logger.LogInformation("Successfully submitted PR review with event {Event}.", reviewEvent);
            }
            catch (ApiException ex) when (reviewPayload.Comments != null && ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            {
                _logger.LogWarning("GitHub rejected inline comments ({Message}). Retrying review with summary note only...", ex.Message);
                reviewPayload.Comments = null;
                await _client.Connection.Post<PullRequestReview>(
                    endpoint,
                    reviewPayload,
                    "application/vnd.github+json",
                    "application/json",
                    (IDictionary<string, string>?)null,
                    cancellationToken);

                _logger.LogInformation("Successfully submitted PR review without inline comments.");
            }
            catch (ApiException ex) when (reviewEvent != PullRequestReviewEvent.Comment &&
                                         (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity ||
                                          ex.StatusCode == System.Net.HttpStatusCode.Forbidden))
            {
                _logger.LogWarning("GitHub rejected {Event} review ({Message}). Retrying review as advisory Comment...", reviewEvent, ex.Message);
                reviewPayload.Event = "COMMENT";
                await _client.Connection.Post<PullRequestReview>(
                    endpoint,
                    reviewPayload,
                    "application/vnd.github+json",
                    "application/json",
                    (IDictionary<string, string>?)null,
                    cancellationToken);

                _logger.LogInformation("Successfully submitted PR review as fallback Comment.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to submit review batch.");
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
        sb.AppendLine($"| **Code Health Score** | **{result.HealthScore} / 100** | {result.HealthProgressBar} |");
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

        // 6. AI Executive Summary (if provided by Gemini)
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
                    var lineDisplay = (comment.StartLine.HasValue && comment.StartLine.Value < comment.Line)
                        ? $"Lines {comment.StartLine}-{comment.Line}"
                        : $"Line {comment.Line}";
                    sb.AppendLine($"#### {icon} `{comment.FilePath}` ({lineDisplay}){titleSnippet}");
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

        // 9. Footer
        sb.AppendLine("---");
        sb.AppendLine("<sub>Reviewed automatically with [Universal PR Bot](https://github.com/marketplace) • Powered by Google Gemini AI & .NET 10</sub>");

        return sb.ToString();
    }

    /// <summary>
    /// Evaluates the review result and maps it to the appropriate GitHub Review Event
    /// based on severity thresholds and configured mode.
    /// </summary>
    public static PullRequestReviewEvent DetermineReviewEvent(ReviewResult result, ReviewEventMode mode = ReviewEventMode.Dynamic)
    {
        if (mode == ReviewEventMode.CommentOnly)
        {
            return PullRequestReviewEvent.Comment;
        }

        if (mode == ReviewEventMode.Strict)
        {
            if (result.CriticalCount > 0 || result.HighCount > 0 || result.MediumCount > 0)
            {
                return PullRequestReviewEvent.RequestChanges;
            }

            return result.Comments.Count == 0
                ? PullRequestReviewEvent.Approve
                : PullRequestReviewEvent.Comment;
        }

        // Dynamic mode (default):
        // Quality Gate failure (Critical or High defects) -> Request Changes
        if (result.CriticalCount > 0 || result.HighCount > 0)
        {
            return PullRequestReviewEvent.RequestChanges;
        }

        // 100% clean PR with zero issues -> Approve
        if (result.Comments.Count == 0)
        {
            return PullRequestReviewEvent.Approve;
        }

        // Advisory issues (Medium or Low severity only) -> Comment
        return PullRequestReviewEvent.Comment;
    }
}

internal sealed class ReviewSubmissionPayload
{
    public required string CommitId { get; set; }
    public required string Body { get; set; }
    public required string Event { get; set; }
    public List<ReviewCommentSubmissionPayload>? Comments { get; set; }
}

internal sealed class ReviewCommentSubmissionPayload
{
    public required string Path { get; init; }
    public required string Body { get; init; }
    public required int Line { get; init; }
    public int? StartLine { get; init; }
    public string Side { get; init; } = "RIGHT";
    public string? StartSide { get; init; }
}
