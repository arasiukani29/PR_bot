namespace PR_Bot.Core.Services;

using PR_Bot.Core.Models;

/// <summary>
/// Service abstraction for interacting with GitHub repositories and pull requests.
/// </summary>
public interface IGitHubService
{
    /// <summary>
    /// Fetches all modified files and patch diffs for the pull request.
    /// </summary>
    Task<PullRequestContext> FetchChangedFilesAsync(
        PullRequestContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts review comments and markdown summary back to the pull request.
    /// </summary>
    Task PostReviewAsync(
        PullRequestContext context,
        ReviewResult result,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Identifies previously reported bot comments that have been resolved by developer changes,
    /// updates resolved comments in-place with resolution status and the code diff,
    /// and optionally deletes active comments from prior runs so they roll forward to the latest review
    /// when new defects exist.
    /// Returns the set of comment IDs that were deleted.
    /// </summary>
    Task<ISet<long>> ReconcileResolvedCommentsAsync(
        PullRequestContext context,
        ReviewResult result,
        bool hasNewDefects = false,
        CancellationToken cancellationToken = default);
}
