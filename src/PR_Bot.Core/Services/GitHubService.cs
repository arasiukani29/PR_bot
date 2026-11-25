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

        // Load previously reported bot findings into context for continuous tracking & reconciliation
        await PopulatePriorDefectsAsync(context, cancellationToken);

        return context;
    }

    public async Task PopulatePriorDefectsAsync(
        PullRequestContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var existingComments = await _client.PullRequest.ReviewComment.GetAll(
                context.Owner,
                context.Repo,
                context.PullRequestNumber);

            var priorDefects = new List<PriorReviewDefect>();
            foreach (var comment in existingComments)
            {
                if (comment.Body == null || comment.InReplyToId.HasValue) continue;

                bool isBot = comment.Body.Contains(FindingMarkerPrefix) ||
                             comment.Body.Contains(ResolvedMarker) ||
                             comment.Body.Contains("[RESOLVED]") ||
                             comment.Body.Contains("Reported by ") ||
                             (comment.User?.Login?.Contains("bot", StringComparison.OrdinalIgnoreCase) == true);

                if (!isBot) continue;

                int line = 0;
                var markerMatch = FindingMarkerRegex.Match(comment.Body);
                if (markerMatch.Success && int.TryParse(markerMatch.Groups["line"].Value, out int parsedLine))
                {
                    line = parsedLine;
                }
                else if (!string.IsNullOrWhiteSpace(comment.DiffHunk))
                {
                    var hunkMatch = System.Text.RegularExpressions.Regex.Match(comment.DiffHunk, @"\+(\d+)");
                    if (hunkMatch.Success && int.TryParse(hunkMatch.Groups[1].Value, out int hunkLine))
                    {
                        line = hunkLine;
                    }
                }

                bool isResolved = comment.Body.Contains(ResolvedMarker) || comment.Body.Contains("[RESOLVED]");

                var severity = AnalyzerSeverity.Warning;
                var title = "Flagged finding";
                if (comment.Body.Contains("[CRITICAL]", StringComparison.OrdinalIgnoreCase))
                {
                    severity = AnalyzerSeverity.Critical;
                    title = "Critical defect";
                }
                else if (comment.Body.Contains("[HIGH]", StringComparison.OrdinalIgnoreCase))
                {
                    severity = AnalyzerSeverity.Error;
                    title = "High priority defect";
                }
                else if (comment.Body.Contains("[LOW]", StringComparison.OrdinalIgnoreCase))
                {
                    severity = AnalyzerSeverity.Info;
                    title = "Low priority suggestion";
                }

                var firstLine = comment.Body.Split('\n')
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) &&
                                         !l.StartsWith("### ✅ [RESOLVED]", StringComparison.OrdinalIgnoreCase) &&
                                         !l.StartsWith("<!--")) ?? "";
                if (!string.IsNullOrWhiteSpace(firstLine) && firstLine.Length < 100)
                {
                    title = firstLine;
                }

                priorDefects.Add(new PriorReviewDefect
                {
                    CommentId = comment.Id,
                    FilePath = comment.Path,
                    Line = line,
                    Title = title,
                    Severity = severity,
                    OriginalBody = comment.Body,
                    IsResolved = isResolved,
                    WasPreviouslyResolved = isResolved
                });
            }

            context.PriorDefects = priorDefects;
            _logger.LogInformation("Loaded {Count} prior bot comments ({Resolved} resolved, {Active} active) for PR #{PrNumber}",
                priorDefects.Count, priorDefects.Count(d => d.IsResolved), priorDefects.Count(d => !d.IsResolved), context.PullRequestNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch prior review comments for defect context.");
        }
    }

    public async Task PostReviewAsync(
        PullRequestContext context,
        ReviewResult result,
        CancellationToken cancellationToken = default)
    {
        // Detect regressions: if an analyzer reported a defect on a file & line that was
        // previously resolved in an earlier commit, the defect has REGRESSED in the latest code!
        bool hasRegressions = false;
        foreach (var finding in result.Comments)
        {
            var regressedPrior = context.PriorDefects.FirstOrDefault(p =>
                p.WasPreviouslyResolved &&
                p.FilePath.Equals(finding.FilePath, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(p.Line - finding.Line) <= 3);

            if (regressedPrior != null)
            {
                _logger.LogWarning(
                    "REGRESSION DETECTED on {File}:{Line}! Previously resolved comment #{CommentId} is defective again in commit {HeadSha}.",
                    regressedPrior.FilePath, regressedPrior.Line, regressedPrior.CommentId, context.HeadSha);

                regressedPrior.IsResolved = false;
                regressedPrior.ResolutionExplanation = null;
                hasRegressions = true;
            }
        }

        // Determine if brand-new defects exist that were not previously reported as active comments
        // Note: Any regression is considered a new defect requiring review notification!
        bool hasNewDefects = hasRegressions || result.Comments.Any(c =>
            !context.PriorDefects.Any(p =>
                !p.IsResolved &&
                p.FilePath.Equals(c.FilePath, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(p.Line - c.Line) <= 3));

        // 1. Reconcile any previously reported comments that have been resolved
        // If hasNewDefects is true: deletes active comments so they roll forward to the new review batch.
        // If hasNewDefects is false: preserves existing active comments and ONLY edits resolved comments!
        var deletedCommentIds = await ReconcileResolvedCommentsAsync(context, result, hasNewDefects, cancellationToken);

        // If an issue was explicitly confirmed resolved by AI in THIS run (via defect_reconciliations),
        // remove any duplicate finding emitted by the AI on that line in this same run.
        var newlyResolved = context.PriorDefects.Where(p => !p.WasPreviouslyResolved && p.IsResolved).ToList();
        if (newlyResolved.Count > 0)
        {
            result.Comments.RemoveAll(c =>
                newlyResolved.Any(r =>
                    r.FilePath.Equals(c.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(r.Line - c.Line) <= 3));
        }

        // Synchronize review result with reconciled defect states
        result.ResolvedDefects.Clear();
        result.ResolvedDefects.AddRange(context.PriorDefects.Where(d => d.IsResolved));

        // Deduplicate ActivePriorDefects against Comments so defects are never double counted
        result.ActivePriorDefects.Clear();
        result.ActivePriorDefects.AddRange(
            context.PriorDefects.Where(d =>
                !d.IsResolved &&
                !result.Comments.Any(c =>
                    c.FilePath.Equals(d.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(c.Line - d.Line) <= 3)));

        // 2. Build the full summary markdown dashboard
        var summaryMarkdown = BuildReviewMarkdown(context, result);

        // 3. Update legacy issue comment if one was created by older bot runs (prevents stale comments)
        await UpsertSummaryCommentAsync(context, summaryMarkdown, cancellationToken);

        // 4. Submit new inline comments and/or update the unified review container (APPROVE/REQUEST_CHANGES)
        // Packaging the complete dashboard in the review body ensures ONE unified review container!
        await PostReviewBatchAsync(context, result, summaryMarkdown, deletedCommentIds, hasNewDefects, cancellationToken);
    }

    public async Task<ISet<long>> ReconcileResolvedCommentsAsync(
        PullRequestContext context,
        ReviewResult result,
        bool hasNewDefects = false,
        CancellationToken cancellationToken = default)
    {
        var deletedCommentIds = new HashSet<long>();
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
            return deletedCommentIds;
        }

        if (existingComments.Count == 0)
        {
            return deletedCommentIds;
        }

        // Identify parent review comments posted by the bot (skip thread replies)
        var botComments = existingComments.Where(c =>
            c.Body != null &&
            !c.InReplyToId.HasValue &&
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

            // Check if the file itself was deleted in this PR
            bool isFileRemoved = changedFile != null &&
                                 changedFile.Status.Equals("removed", StringComparison.OrdinalIgnoreCase);

            // Check if AI explicitly reconciled this defect as resolved or still active
            var priorMatch = context.PriorDefects.FirstOrDefault(d => d.CommentId == comment.Id);
            bool aiSaysResolved = priorMatch?.IsResolved == true;
            bool aiSaysStillActive = priorMatch != null && string.Equals(priorMatch.ReconciliationStatus, "still_active", StringComparison.OrdinalIgnoreCase);

            // Check if the lines around commentLine were modified in the patch or if GitHub marked the comment as Outdated
            bool lineWasModified = (comment.Position == null) ||
                                  (changedFile?.Patch != null &&
                                   changedFile.GetAddedOrModifiedLines().Any(l => Math.Abs(l - commentLine) <= 10));

            // Check if defect is still reported in current review findings (by line proximity or matching title in the same file)
            bool sameTitleInFile = priorMatch != null &&
                                   !string.IsNullOrWhiteSpace(priorMatch.Title) &&
                                   result.Comments.Any(c =>
                                       c.FilePath.Equals(comment.Path, StringComparison.OrdinalIgnoreCase) &&
                                       !string.IsNullOrWhiteSpace(c.Title) &&
                                       c.Title.Equals(priorMatch.Title, StringComparison.OrdinalIgnoreCase));

            bool isStillDefective = sameTitleInFile || result.Comments.Any(c =>
                c.FilePath.Equals(comment.Path, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(c.Line - commentLine) <= 10);

            // A defect is resolved if:
            // 1. The file was removed in this PR, OR
            // 2. AI explicitly verified it as resolved (aiSaysResolved), OR
            // 3. AI did NOT explicitly confirm it as still active, the code was modified/outdated, the file is in the PR, and NO defect was found on that code in this run.
            bool isResolved = isFileRemoved || aiSaysResolved || (!aiSaysStillActive && !isStillDefective && lineWasModified && changedFile != null);

            if (isResolved)
            {
                _logger.LogInformation(
                    "Comment #{CommentId} on {Path}:{Line} detected as RESOLVED. Updating comment in-place...",
                    comment.Id, comment.Path, commentLine);

                // Fetch commit patch for HeadSha to get exact lines added (+) and deleted (-) for the fix
                string? patchToUse = null;
                if (!string.IsNullOrWhiteSpace(context.HeadSha) &&
                    !string.Equals(context.HeadSha, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var commit = await _client.Repository.Commit.Get(context.Owner, context.Repo, context.HeadSha);
                        var commitFile = commit.Files?.FirstOrDefault(f =>
                            f.Filename.Equals(comment.Path, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrWhiteSpace(commitFile?.Patch))
                        {
                            patchToUse = commitFile.Patch;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not fetch commit patch for {HeadSha} on {Path}. Falling back to PR file patch.", context.HeadSha, comment.Path);
                    }
                }

                patchToUse ??= changedFile?.Patch;

                // Extract the diff of code added/modified that resolved this issue
                var diffSnippet = _patchExtractor.ExtractResolutionDiff(patchToUse, commentLine);

                var commitSnippet = !string.IsNullOrWhiteSpace(context.HeadSha) && context.HeadSha.Length >= 7
                    ? context.HeadSha[..7]
                    : context.HeadSha ?? "latest commit";

                var explanationBlock = !string.IsNullOrWhiteSpace(priorMatch?.ResolutionExplanation)
                    ? $"\n> 💡 **AI Verification:** {priorMatch.ResolutionExplanation}\n"
                    : "";

                // 1. Edit the parent comment header to indicate [RESOLVED] while keeping original finding details intact
                var resolvedHeader = isFileRemoved ? "### ✅ [RESOLVED - File Removed]" : "### ✅ [RESOLVED]";
                string updatedBody = comment.Body.Contains("[RESOLVED]")
                    ? comment.Body
                    : $"{resolvedHeader}\n\n{comment.Body}\n\n{ResolvedMarker}";

                try
                {
                    await _client.PullRequest.ReviewComment.Edit(
                        context.Owner,
                        context.Repo,
                        comment.Id,
                        new PullRequestReviewCommentEdit(updatedBody));

                    _logger.LogInformation("Successfully updated comment #{CommentId} to [RESOLVED].", comment.Id);

                    if (priorMatch != null)
                    {
                        priorMatch.IsResolved = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to edit review comment #{CommentId} on {Path}.", comment.Id, comment.Path);
                }

                // 2. Post thread reply formatted as requested:
                //    ✅ Resolved in `01f4c11`.
                //    <details open>
                //    <summary>Changes that fixed it</summary>
                //    ```diff
                //    ...
                //    ```
                //    </details>
                string replyBody = !string.IsNullOrWhiteSpace(diffSnippet)
                    ? $"""
                      ✅ Resolved in `{commitSnippet}`.
                      {explanationBlock}
                      <details open>
                      <summary>Changes that fixed it</summary>

                      ```diff
                      {diffSnippet}
                      ```
                      </details>

                      {ResolvedMarker}
                      """
                    : $"""
                      ✅ Resolved in `{commitSnippet}`.
                      {explanationBlock}
                      *Fix verified by automated review.*

                      {ResolvedMarker}
                      """;

                try
                {
                    await _client.PullRequest.ReviewComment.CreateReply(
                        context.Owner,
                        context.Repo,
                        context.PullRequestNumber,
                        new PullRequestReviewCommentReplyCreate(replyBody, comment.Id));

                    _logger.LogInformation("Successfully posted resolution reply to comment thread #{CommentId}.", comment.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to post thread reply to review comment #{CommentId}.", comment.Id);
                }

                // 3. Resolve review thread in GitHub natively via GraphQL
                await ResolveThreadViaGraphQLAsync(context.Owner, context.Repo, context.PullRequestNumber, comment.Id, cancellationToken);
            }
            else
            {
                // Defect is STILL ACTIVE from an earlier review run.
                // We PRESERVE this existing comment untouched on GitHub so thread history and developer replies are preserved!
                _logger.LogInformation(
                    "Preserving existing active comment #{CommentId} on {Path}:{Line} (keeping thread history intact).",
                    comment.Id, comment.Path, commentLine);
            }
        }

        return deletedCommentIds;
    }

    private async Task PostReviewBatchAsync(
        PullRequestContext context,
        ReviewResult result,
        string summaryMarkdown,
        ISet<long> deletedCommentIds,
        bool hasNewDefects,
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

        // Only deduplicate against ACTIVE (non-resolved) comments that were NOT deleted in this run.
        // If a comment was previously marked [RESOLVED], re-introducing the bug is a REGRESSION,
        // and must be reported as a fresh defect!
        var activeExistingComments = existingComments.Where(e =>
            !deletedCommentIds.Contains(e.Id) &&
            e.Body != null &&
            !e.Body.Contains(ResolvedMarker) &&
            !e.Body.Contains("[RESOLVED]")).ToList();

        // Collect comments that have valid lines and haven't already been reported
        var inlineComments = new List<ReviewCommentSubmissionPayload>();

        foreach (var comment in result.Comments)
        {
            if (comment.Line > 0)
            {
                var marker = $"{FindingMarkerPrefix}file={comment.FilePath}:line={comment.Line} -->";
                var body = $"{comment.Body}\n\n<sub>Reported by {comment.AnalyzerName}</sub>\n{marker}";

                // Check if this exact finding has already been posted to avoid duplicate spam across runs
                var snippet = comment.Body.Length > 80 ? comment.Body[..80] : comment.Body;
                bool isDuplicate = activeExistingComments.Any(e =>
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
                    _logger.LogInformation("Skipping already reported active comment on {File} (Line {Line})", comment.FilePath, comment.Line);
                }
            }
        }

        // Only roll forward active prior defects if new defects exist and they are not already posted on PR.
        if (hasNewDefects)
        {
            foreach (var activeDefect in result.ActivePriorDefects)
            {
                if (activeDefect.Line > 0)
                {
                    var marker = $"{FindingMarkerPrefix}file={activeDefect.FilePath}:line={activeDefect.Line} -->";

                    bool alreadyInComments = inlineComments.Any(c =>
                        c.Path.Equals(activeDefect.FilePath, StringComparison.OrdinalIgnoreCase) &&
                        c.Line == activeDefect.Line);

                    bool alreadyExistsOnPr = activeExistingComments.Any(e =>
                        e.Path.Equals(activeDefect.FilePath, StringComparison.OrdinalIgnoreCase) &&
                        (e.Id == activeDefect.CommentId || e.Body.Contains(marker)));

                    if (!alreadyInComments && !alreadyExistsOnPr)
                    {
                        inlineComments.Add(new ReviewCommentSubmissionPayload
                        {
                            Path = activeDefect.FilePath,
                            Body = activeDefect.OriginalBody.Contains(marker)
                                ? activeDefect.OriginalBody
                                : $"{activeDefect.OriginalBody}\n\n<sub>Reported by Gemini AI</sub>\n{marker}",
                            Line = activeDefect.Line,
                            StartLine = null,
                            Side = "RIGHT",
                            StartSide = null
                        });

                        _logger.LogInformation(
                            "Rolled forward active defect #{CommentId} on {Path}:{Line} to latest review.",
                            activeDefect.CommentId, activeDefect.FilePath, activeDefect.Line);
                    }
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

        // Check previous reviews submitted by the bot
        PullRequestReview? latestBotReview = null;
        try
        {
            var existingReviews = await _client.PullRequest.Review.GetAll(
                context.Owner,
                context.Repo,
                context.PullRequestNumber);

            latestBotReview = existingReviews
                .Where(r => r.User != null && (string.IsNullOrWhiteSpace(r.User.Login) || r.User.Login.Contains("bot", StringComparison.OrdinalIgnoreCase) || r.User.Login == "github-actions[bot]"))
                .OrderByDescending(r => r.SubmittedAt)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check prior review state.");
        }

        bool stateMatches = latestBotReview != null && reviewEvent switch
        {
            PullRequestReviewEvent.Approve => latestBotReview.State.Value == PullRequestReviewState.Approved ||
                                              string.Equals(latestBotReview.State.StringValue, "APPROVED", StringComparison.OrdinalIgnoreCase),
            PullRequestReviewEvent.RequestChanges => latestBotReview.State.Value == PullRequestReviewState.ChangesRequested ||
                                                     string.Equals(latestBotReview.State.StringValue, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase),
            _ => latestBotReview.State.Value == PullRequestReviewState.Commented ||
                 string.Equals(latestBotReview.State.StringValue, "COMMENTED", StringComparison.OrdinalIgnoreCase)
        };

        // If the review state is unchanged and there are no new inline comments, do not submit a duplicate review!
        if (latestBotReview != null && stateMatches && inlineComments.Count == 0)
        {
            _logger.LogInformation("Review status unchanged ({Event}) and no new inline comments. Overall summary updated in-place.", reviewEventStr);
            return;
        }

        string reviewBody = reviewEvent switch
        {
            PullRequestReviewEvent.RequestChanges => "Changes requested based on automated review findings. Please see inline comments and the overall summary above.",
            PullRequestReviewEvent.Approve => "Approved based on automated review findings. All blockers resolved.",
            _ => "Automated code review completed. Please see inline comments and the overall summary above."
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
                Body = reviewBody,
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
            var existingComments = await _client.Issue.Comment.GetAllForIssue(
                context.Owner,
                context.Repo,
                context.PullRequestNumber);

            var previousBotComment = existingComments?.FirstOrDefault(c =>
                c.Body != null && c.Body.Contains(BotCommentMarker));

            if (previousBotComment != null)
            {
                _logger.LogInformation("Updating overall PR review comment ID {Id} in-place...", previousBotComment.Id);
                await _client.Issue.Comment.Update(
                    context.Owner,
                    context.Repo,
                    previousBotComment.Id,
                    summaryMarkdown);
                _logger.LogInformation("Updated overall review comment in-place.");
            }
            else
            {
                _logger.LogInformation("Creating single overall PR review comment for PR #{PrNumber}...", context.PullRequestNumber);
                await _client.Issue.Comment.Create(
                    context.Owner,
                    context.Repo,
                    context.PullRequestNumber,
                    summaryMarkdown);
                _logger.LogInformation("Created single overall review comment.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not upsert overall summary comment on PR #{PrNumber}", context.PullRequestNumber);
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
        int blockerCount = criticalCount + highCount;
        bool isBlocked = blockerCount > 0;

        var sb = new StringBuilder();

        // 1. Hidden marker to identify bot comments for upsert
        sb.AppendLine(BotCommentMarker);

        // 2. Status Header & 1-Line Verdict
        if (isBlocked)
        {
            sb.AppendLine("### ❌ PR Review: Merge Blocked");
            sb.AppendLine();
            sb.AppendLine($"{blockerCount} blocking issue{(blockerCount > 1 ? "s" : "")} require attention before this pull request can be merged.");
        }
        else if (mediumCount > 0 || lowCount > 0)
        {
            sb.AppendLine("### 🟡 PR Review: Action Recommended");
            sb.AppendLine();
            sb.AppendLine($"No blocking issues found. {mediumCount + lowCount} advisory item{(mediumCount + lowCount > 1 ? "s" : "")} available for review.");
        }
        else
        {
            sb.AppendLine("### ✅ PR Review: Approved to Merge");
            sb.AppendLine();
            sb.AppendLine("All automated quality and security checks have passed with zero blockers. Ready to merge! 🎉");
        }
        sb.AppendLine();

        // 3. Option A Category Badges: Security · Reliability · Code Quality
        var allActiveFindings = result.Comments
            .Concat(result.ActivePriorDefects.Select(d => new ReviewComment
            {
                FilePath = d.FilePath,
                Line = d.Line,
                Title = d.Title,
                Severity = d.Severity,
                AnalyzerName = "Prior Defect",
                Body = d.OriginalBody
            })).ToList();

        bool hasSecurityIssue = allActiveFindings.Any(c =>
            c.Severity >= AnalyzerSeverity.Error && (
                string.Equals(c.Category, "Security", StringComparison.OrdinalIgnoreCase) ||
                (c.Title != null && c.Title.Contains("Security", StringComparison.OrdinalIgnoreCase)) ||
                c.Body.Contains("Security", StringComparison.OrdinalIgnoreCase) ||
                c.Body.Contains("injection", StringComparison.OrdinalIgnoreCase) ||
                c.Body.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                c.Body.Contains("password", StringComparison.OrdinalIgnoreCase)));

        bool hasReliabilityIssue = allActiveFindings.Any(c =>
            string.Equals(c.Category, "Reliability", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Category, "Input Validation", StringComparison.OrdinalIgnoreCase) ||
            (c.Title != null && (c.Title.Contains("Null", StringComparison.OrdinalIgnoreCase) ||
                                 c.Title.Contains("Crash", StringComparison.OrdinalIgnoreCase) ||
                                 c.Title.Contains("Loop", StringComparison.OrdinalIgnoreCase) ||
                                 c.Title.Contains("Exception", StringComparison.OrdinalIgnoreCase))) ||
            (c.Severity >= AnalyzerSeverity.Warning && !hasSecurityIssue));

        bool hasCodeQualityIssue = allActiveFindings.Any(c =>
            string.Equals(c.Category, "Maintainability", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Category, "Performance", StringComparison.OrdinalIgnoreCase) ||
            c.Severity == AnalyzerSeverity.Info);

        bool hasCriticalReliability = allActiveFindings.Any(c =>
            c.Severity >= AnalyzerSeverity.Error && (
                string.Equals(c.Category, "Reliability", StringComparison.OrdinalIgnoreCase) ||
                (c.Title != null && (c.Title.Contains("Crash", StringComparison.OrdinalIgnoreCase) ||
                                     c.Title.Contains("Deadlock", StringComparison.OrdinalIgnoreCase) ||
                                     c.Title.Contains("Infinite loop", StringComparison.OrdinalIgnoreCase)))));

        string secBadge = hasSecurityIssue ? "❌ Security" : "✅ Security";
        string relBadge = hasCriticalReliability ? "❌ Reliability" : (hasReliabilityIssue ? "⚠️ Reliability" : "✅ Reliability");
        string qualBadge = hasCodeQualityIssue ? "⚠️ Code Quality" : "✅ Code Quality";

        sb.AppendLine($"{secBadge} · {relBadge} · {qualBadge}");
        sb.AppendLine();

        // 4. Priority Sections (Critical/High, Medium, Low)
        var blockers = allActiveFindings.Where(c => c.Severity >= AnalyzerSeverity.Error).ToList();
        if (blockers.Count > 0)
        {
            sb.AppendLine("🔴 **Action Required (Critical & High Priority)**");
            foreach (var item in blockers)
            {
                var link = BuildFileLink(context, item.FilePath, item.Line, item.StartLine);
                var title = !string.IsNullOrWhiteSpace(item.Title) ? item.Title : "Critical/High priority defect";
                sb.AppendLine($"• {title} · {link} ↗");
            }
            sb.AppendLine();
        }

        var mediumIssues = allActiveFindings.Where(c => c.Severity == AnalyzerSeverity.Warning).ToList();
        if (mediumIssues.Count > 0)
        {
            sb.AppendLine("🟡 **Recommended Improvements (Medium Priority)**");
            foreach (var item in mediumIssues)
            {
                var link = BuildFileLink(context, item.FilePath, item.Line, item.StartLine);
                var title = !string.IsNullOrWhiteSpace(item.Title) ? item.Title : "Medium priority recommendation";
                sb.AppendLine($"• {title} · {link} ↗");
            }
            sb.AppendLine();
        }

        var lowIssues = allActiveFindings.Where(c => c.Severity == AnalyzerSeverity.Info).ToList();
        if (lowIssues.Count > 0)
        {
            sb.AppendLine("🟢 **Optional Suggestions (Low Priority)**");
            foreach (var item in lowIssues)
            {
                var link = BuildFileLink(context, item.FilePath, item.Line, item.StartLine);
                var title = !string.IsNullOrWhiteSpace(item.Title) ? item.Title : "Optional suggestion";
                sb.AppendLine($"• {title} · {link} ↗");
            }
            sb.AppendLine();
        }

        // 5. Collapsible Verified Fixes Section
        if (result.ResolvedDefects.Count > 0)
        {
            int count = result.ResolvedDefects.Count;
            sb.AppendLine("<details>");
            sb.AppendLine($"<summary><b>✅ {count} verified fix{(count > 1 ? "es" : "")}</b></summary>");
            sb.AppendLine();

            var commitSnippet = !string.IsNullOrWhiteSpace(context.HeadSha) && context.HeadSha.Length >= 7
                ? context.HeadSha[..7]
                : context.HeadSha ?? "latest commit";

            foreach (var resolved in result.ResolvedDefects)
            {
                var link = BuildFileLink(context, resolved.FilePath, resolved.Line);
                var note = !string.IsNullOrWhiteSpace(resolved.ResolutionExplanation)
                    ? $" *(Fixed in `{commitSnippet}`: {resolved.ResolutionExplanation})*"
                    : $" *(Fixed in `{commitSnippet}`)*";
                sb.AppendLine($"• {resolved.Title} · {link}{note}");
            }
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        // 6. AI Summary if provided
        if (!string.IsNullOrWhiteSpace(result.SummaryMarkdown))
        {
            sb.AppendLine($"> 💡 {result.SummaryMarkdown.Trim().Replace("\n", " ")}");
            sb.AppendLine();
        }

        // 7. Footer & hidden tracking marker
        var shortSha = !string.IsNullOrWhiteSpace(context.HeadSha) && context.HeadSha.Length >= 7
            ? context.HeadSha[..7]
            : context.HeadSha ?? "latest";
        sb.AppendLine("---");
        sb.AppendLine($"<sub>Universal PR Bot · Evaluated commit `{shortSha}`</sub>");
        sb.AppendLine($"<!-- pr-bot-summary:commit={shortSha}:verdict={(isBlocked ? "FAIL" : "PASS")} -->");

        return sb.ToString();
    }

    private static string BuildFileLink(PullRequestContext context, string filePath, int line, int? startLine = null)
    {
        if (startLine.HasValue && startLine.Value < line)
        {
            var lineRange = $"Lines {startLine.Value}-{line}";
            if (!string.IsNullOrWhiteSpace(context.Owner) &&
                !string.IsNullOrWhiteSpace(context.Repo) &&
                !string.IsNullOrWhiteSpace(context.HeadSha))
            {
                return $"[{filePath}:{startLine.Value}-{line}](https://github.com/{context.Owner}/{context.Repo}/blob/{context.HeadSha}/{filePath}#L{startLine.Value}-L{line}) (`{filePath}` ({lineRange}))";
            }
            return $"`{filePath}` ({lineRange})";
        }

        if (line <= 0) return $"`{filePath}`";

        if (!string.IsNullOrWhiteSpace(context.Owner) &&
            !string.IsNullOrWhiteSpace(context.Repo) &&
            !string.IsNullOrWhiteSpace(context.HeadSha))
        {
            return $"[{filePath}:{line}](https://github.com/{context.Owner}/{context.Repo}/blob/{context.HeadSha}/{filePath}#L{line})";
        }

        return $"`{filePath}:{line}`";
    }

    private async Task<string?> FindGraphQLThreadIdAsync(
        string owner,
        string repo,
        int prNumber,
        long commentId,
        bool? requiredResolvedState,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = """
                query ($owner: String!, $repo: String!, $pr: Int!, $after: String) {
                  repository(owner: $owner, name: $repo) {
                    pullRequest(number: $pr) {
                      reviewThreads(first: 100, after: $after) {
                        pageInfo {
                          hasNextPage
                          endCursor
                        }
                        nodes {
                          id
                          isResolved
                          comments(first: 100) {
                            nodes {
                              databaseId
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var endpoint = new Uri("graphql", UriKind.Relative);
            string? cursor = null;
            bool hasNextPage = true;

            while (hasNextPage)
            {
                var queryPayload = new
                {
                    query,
                    variables = new { owner, repo, pr = prNumber, after = cursor }
                };

                var response = await _client.Connection.Post<object>(
                    endpoint,
                    queryPayload,
                    "application/vnd.github+json",
                    "application/json",
                    (IDictionary<string, string>?)null,
                    cancellationToken);

                string? rawJson = response?.HttpResponse?.Body?.ToString();
                if (string.IsNullOrWhiteSpace(rawJson) && response?.Body != null)
                {
                    rawJson = System.Text.Json.JsonSerializer.Serialize(response.Body);
                }

                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    return null;
                }

                using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
                if (doc.RootElement.TryGetProperty("errors", out var errors))
                {
                    _logger.LogWarning("GitHub GraphQL query returned errors: {Errors}", errors.ToString());
                    return null;
                }

                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("repository", out var repoElem) ||
                    !repoElem.TryGetProperty("pullRequest", out var prElem) ||
                    !prElem.TryGetProperty("reviewThreads", out var reviewThreads) ||
                    !reviewThreads.TryGetProperty("nodes", out var threadNodes))
                {
                    return null;
                }

                foreach (var thread in threadNodes.EnumerateArray())
                {
                    if (requiredResolvedState.HasValue)
                    {
                        bool isRes = thread.TryGetProperty("isResolved", out var isResProp) && isResProp.GetBoolean();
                        if (isRes != requiredResolvedState.Value)
                        {
                            continue;
                        }
                    }

                    if (!thread.TryGetProperty("id", out var idProp))
                    {
                        continue;
                    }

                    if (thread.TryGetProperty("comments", out var commentsElem) &&
                        commentsElem.TryGetProperty("nodes", out var commentNodes))
                    {
                        foreach (var c in commentNodes.EnumerateArray())
                        {
                            if (c.TryGetProperty("databaseId", out var dbId) && dbId.GetInt64() == commentId)
                            {
                                return idProp.GetString();
                            }
                        }
                    }
                }

                hasNextPage = reviewThreads.TryGetProperty("pageInfo", out var pageInfo) &&
                              pageInfo.TryGetProperty("hasNextPage", out var hasNextProp) &&
                              hasNextProp.GetBoolean();

                cursor = hasNextPage && pageInfo.TryGetProperty("endCursor", out var endCursorProp)
                    ? endCursorProp.GetString()
                    : null;

                if (hasNextPage && string.IsNullOrWhiteSpace(cursor))
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query GraphQL review threads for comment #{CommentId}.", commentId);
        }

        return null;
    }

    private async Task ResolveThreadViaGraphQLAsync(
        string owner,
        string repo,
        int prNumber,
        long commentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var threadId = await FindGraphQLThreadIdAsync(owner, repo, prNumber, commentId, requiredResolvedState: false, cancellationToken);
            if (string.IsNullOrWhiteSpace(threadId))
            {
                return;
            }

            var mutation = """
                mutation ($threadId: ID!) {
                  resolveReviewThread(input: {threadId: $threadId}) {
                    thread {
                      id
                      isResolved
                    }
                  }
                }
                """;

            var endpoint = new Uri("graphql", UriKind.Relative);
            var mutationPayload = new
            {
                query = mutation,
                variables = new { threadId }
            };

            await _client.Connection.Post<object>(
                endpoint,
                mutationPayload,
                "application/vnd.github+json",
                "application/json",
                (IDictionary<string, string>?)null,
                cancellationToken);

            _logger.LogInformation("Successfully resolved GitHub review thread {ThreadId} for comment #{CommentId} via GraphQL.", threadId, commentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve review thread via GraphQL for comment #{CommentId}. (Thread reply was posted via REST).", commentId);
        }
    }

    private async Task UnresolveThreadViaGraphQLAsync(
        string owner,
        string repo,
        int prNumber,
        long commentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var threadId = await FindGraphQLThreadIdAsync(owner, repo, prNumber, commentId, requiredResolvedState: true, cancellationToken);
            if (string.IsNullOrWhiteSpace(threadId))
            {
                return;
            }

            var mutation = """
                mutation ($threadId: ID!) {
                  unresolveReviewThread(input: {threadId: $threadId}) {
                    thread {
                      id
                      isResolved
                    }
                  }
                }
                """;

            var endpoint = new Uri("graphql", UriKind.Relative);
            var mutationPayload = new
            {
                query = mutation,
                variables = new { threadId }
            };

            await _client.Connection.Post<object>(
                endpoint,
                mutationPayload,
                "application/vnd.github+json",
                "application/json",
                (IDictionary<string, string>?)null,
                cancellationToken);

            _logger.LogInformation("Successfully un-resolved GitHub review thread {ThreadId} for regressed comment #{CommentId} via GraphQL.", threadId, commentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not unresolve review thread via GraphQL for comment #{CommentId}.", commentId);
        }
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

            return result.TotalDefects == 0
                ? PullRequestReviewEvent.Approve
                : PullRequestReviewEvent.Comment;
        }

        // Dynamic mode (default):
        // Quality Gate failure (Critical or High defects) -> Request Changes
        if (result.CriticalCount > 0 || result.HighCount > 0)
        {
            return PullRequestReviewEvent.RequestChanges;
        }

        // 100% clean PR with zero issues (both new and prior active) -> Approve
        if (result.TotalDefects == 0)
        {
            return PullRequestReviewEvent.Approve;
        }

        // Advisory issues (Medium or Low severity only) -> Comment
        return PullRequestReviewEvent.Comment;
    }
}

public sealed class ReviewSubmissionPayload
{
    public required string CommitId { get; set; }
    public required string Body { get; set; }
    public required string Event { get; set; }
    public List<ReviewCommentSubmissionPayload>? Comments { get; set; }
}

public sealed class ReviewCommentSubmissionPayload
{
    public required string Path { get; init; }
    public required string Body { get; init; }
    public required int Line { get; init; }
    public int? StartLine { get; init; }
    public string Side { get; init; } = "RIGHT";
    public string? StartSide { get; init; }
}
