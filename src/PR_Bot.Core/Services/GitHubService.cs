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

        var sb = new StringBuilder();

        // Hidden marker to identify bot comments and update them on re-runs
        sb.AppendLine(BotCommentMarker);
        sb.AppendLine("## 🤖 Universal PR Review Bot Summary");
        sb.AppendLine();
        sb.AppendLine($"> Reviewing PR **#{context.PullRequestNumber}**: `{context.Title}` by @{context.Author}");
        sb.AppendLine();

        // Code Health Score Section
        var healthIcon = result.HealthScore switch
        {
            >= 90 => "🟢",
            >= 75 => "🟡",
            >= 50 => "🟠",
            _ => "🔴"
        };

        sb.AppendLine($"### {healthIcon} Code Health Score: **{result.HealthScore}/100** — Grade: **{result.HealthGrade}**");
        sb.AppendLine("```text");
        sb.AppendLine(result.HealthProgressBar);
        sb.AppendLine("```");
        sb.AppendLine();

        // Priority Metrics Table (4 Tiers)
        sb.AppendLine("| Status | Priority Level | Issues |");
        sb.AppendLine("|:------:|:---------------|:------:|");
        sb.AppendLine($"| {(criticalCount > 0 ? "🚨" : "✅")} | 🚨 **Critical** (Immediate Blocker) | **{criticalCount}** |");
        sb.AppendLine($"| {(highCount > 0 ? "🔴" : "✅")} | 🔴 **High Priority** (Must Resolve) | **{highCount}** |");
        sb.AppendLine($"| {(mediumCount > 0 ? "🟡" : "✅")} | 🟡 **Medium Priority** (Should Resolve) | **{mediumCount}** |");
        sb.AppendLine($"| {(lowCount > 0 ? "🟢" : "✅")} | 🟢 **Low Priority** (Suggestions) | **{lowCount}** |");
        sb.AppendLine();

        // Gemini AI Section if available
        if (!string.IsNullOrWhiteSpace(result.SummaryMarkdown))
        {
            sb.AppendLine("### 🧠 AI Code Review (Google Gemini)");
            sb.AppendLine();
            sb.AppendLine(result.SummaryMarkdown);
            sb.AppendLine();
        }

        // Findings Grouped by Priority
        if (result.Comments.Count > 0)
        {
            sb.AppendLine("### 📋 Review Findings (Ordered by Priority)");
            sb.AppendLine();

            var priorityGroups = result.Comments
                .GroupBy(c => c.Severity)
                .OrderByDescending(g => g.Key); // Critical -> High -> Medium -> Low

            foreach (var group in priorityGroups)
            {
                var (title, icon) = group.Key switch
                {
                    AnalyzerSeverity.Critical => ("🚨 Critical Blockers (Immediate Security/Crash Hazards)", "🚨"),
                    AnalyzerSeverity.Error => ("🔴 High Priority Issues (Must Resolve Before Merge)", "🔴"),
                    AnalyzerSeverity.Warning => ("🟡 Medium Priority Issues (Recommended to Fix)", "⚠️"),
                    _ => ("🟢 Low Priority Suggestions (Improvements)", "💡")
                };

                sb.AppendLine($"<details open>");
                sb.AppendLine($"<summary><b>{title} ({group.Count()})</b></summary>");
                sb.AppendLine();

                foreach (var comment in group)
                {
                    sb.AppendLine($"#### {icon} `{comment.FilePath}` (Line {comment.Line})");
                    sb.AppendLine();
                    sb.AppendLine(comment.Body);
                    sb.AppendLine();
                }

                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        if (criticalCount == 0 && highCount == 0 && mediumCount == 0 && lowCount == 0)
        {
            sb.AppendLine("✨ **No code quality or security issues detected! Clean PR.** 🎉");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("<sub>Reviewed automatically with [Universal PR Bot](https://github.com/marketplace). Configure rules in your repo workflow.</sub>");

        return sb.ToString();
    }
}
