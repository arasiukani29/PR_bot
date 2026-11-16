namespace PR_Bot.Core.Services;

using PR_Bot.Core.Models;

/// <summary>
/// Orchestrates the execution of all registered analyzers on a pull request.
/// </summary>
public interface IReviewEngine
{
    /// <summary>
    /// Executes all analyzers against the pull request and compiles the aggregated ReviewResult.
    /// </summary>
    Task<ReviewResult> RunAsync(
        PullRequestContext context,
        CancellationToken cancellationToken = default);
}
