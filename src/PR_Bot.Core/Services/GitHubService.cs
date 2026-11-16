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

            changedFiles.Add(new PullRequestFileChange
            {
                FileName = file.FileName,
                Status = file.Status,
                Additions = file.Additions,
                Deletions = file.Deletions,
                Changes = file.Changes,
                Patch = file.Patch
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

        // 1. Post Inline Comments on PR Files Tab (via PullRequestReviewCreate)
        await PostInlineReviewAsync(context, result, cancellationToken);

        // 2. Post / Update the Single Summary Comment on PR Discussion Tab
        await UpsertSummaryCommentAsync(context, summaryMarkdown, cancellationToken);
    }

    private async Task PostInlineReviewAsync(
        PullRequestContext context,
        ReviewResult result,
        CancellationToken cancellationToken)
    {
        // Collect comments that have valid diff positions
        var inlineComments = new List<DraftPullRequestReviewComment>();

        foreach (var comment in result.Comments)
        {
            if (comment.DiffPosition.HasValue && comment.DiffPosition.Value > 0)
            {
                var body = $"{comment.Body}\n\n<sub>Reported by {comment.AnalyzerName}</sub>";
                inlineComments.Add(new DraftPullRequestReviewComment(body, comment.FilePath, comment.DiffPosition.Value));
            }
        }

        if (inlineComments.Count == 0)
        {
            _logger.LogInformation("No inline diff comments to submit.");
            return;
        }

        _logger.LogInformation(
            "Submitting {Count} inline review comment(s) to PR #{PrNumber}...",
            inlineComments.Count, context.PullRequestNumber);

        try
        {
            var review = new PullRequestReviewCreate
            {
                CommitId = context.HeadSha,
                Event = PullRequestReviewEvent.Comment,
                Body = "🤖 **PR Bot Inline Review:** Findings attached directly to changed lines.",
                Comments = inlineComments
            };

            await _client.PullRequest.Review.Create(
                context.Owner,
                context.Repo,
                context.PullRequestNumber,
                review);

            _logger.LogInformation("Successfully posted inline review comments.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to submit inline review comments batch. Falling back to summary comment.");
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
        var errorCount = result.Comments.Count(c => c.Severity == AnalyzerSeverity.Error);
        var warningCount = result.Comments.Count(c => c.Severity == AnalyzerSeverity.Warning);
        var infoCount = result.Comments.Count(c => c.Severity == AnalyzerSeverity.Info);

        var sb = new StringBuilder();

        // Hidden marker to identify bot comments and update them on re-runs
        sb.AppendLine(BotCommentMarker);
        sb.AppendLine("## 🤖 Universal PR Review Bot Summary");
        sb.AppendLine();
        sb.AppendLine($"> Reviewing PR **#{context.PullRequestNumber}**: `{context.Title}` by @{context.Author}");
        sb.AppendLine();

        // Metrics Table
        sb.AppendLine("| Status | Severity | Count |");
        sb.AppendLine("|:------:|:---------|:-----:|");
        sb.AppendLine($"| {(errorCount > 0 ? "❌" : "✅")} | 🚨 Errors | **{errorCount}** |");
        sb.AppendLine($"| {(warningCount > 0 ? "⚠️" : "✅")} | ⚠️ Warnings | **{warningCount}** |");
        sb.AppendLine($"| 💡 | 💡 Info & Suggestions | **{infoCount}** |");
        sb.AppendLine();

        // Gemini AI Section if available
        if (!string.IsNullOrWhiteSpace(result.SummaryMarkdown))
        {
            sb.AppendLine("### 🧠 AI Code Review (Google Gemini)");
            sb.AppendLine();
            sb.AppendLine(result.SummaryMarkdown);
            sb.AppendLine();
        }

        // Findings per Analyzer
        if (result.Comments.Count > 0)
        {
            sb.AppendLine("### 📋 Static & Rule-Based Findings");
            sb.AppendLine();

            var groups = result.Comments.GroupBy(c => c.AnalyzerName);
            foreach (var group in groups)
            {
                sb.AppendLine($"<details open>");
                sb.AppendLine($"<summary><b>{group.Key} ({group.Count()})</b></summary>");
                sb.AppendLine();

                foreach (var comment in group)
                {
                    var icon = comment.Severity switch
                    {
                        AnalyzerSeverity.Error => "🚨",
                        AnalyzerSeverity.Warning => "⚠️",
                        _ => "💡"
                    };

                    sb.AppendLine($"- {icon} **`{comment.FilePath}`** (Line {comment.Line}): {comment.Body}");
                }

                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        if (errorCount == 0 && warningCount == 0 && result.Comments.Count == 0)
        {
            sb.AppendLine("✨ **No code quality or security issues detected! Clean PR.** 🎉");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("<sub>Reviewed automatically with [Universal PR Bot](https://github.com/marketplace). Configure rules in your repo workflow.</sub>");

        return sb.ToString();
    }
}
