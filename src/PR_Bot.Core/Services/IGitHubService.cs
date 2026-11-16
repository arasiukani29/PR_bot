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
}
