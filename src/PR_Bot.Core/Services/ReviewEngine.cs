namespace PR_Bot.Core.Services;

using Microsoft.Extensions.Logging;
using PR_Bot.Core.Analyzers;
using PR_Bot.Core.Models;

/// <summary>
/// Default implementation of the review orchestrator.
/// </summary>
public sealed class ReviewEngine : IReviewEngine
{
    private readonly IEnumerable<IReviewAnalyzer> _analyzers;
    private readonly ILogger<ReviewEngine> _logger;

    public ReviewEngine(
        IEnumerable<IReviewAnalyzer> analyzers,
        ILogger<ReviewEngine>? logger = null)
    {
        _analyzers = analyzers;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ReviewEngine>.Instance;
    }

    public async Task<ReviewResult> RunAsync(
        PullRequestContext context,
        CancellationToken cancellationToken = default)
    {
        var sortedAnalyzers = _analyzers.OrderBy(a => a.Order).ToList();
        var allComments = new List<ReviewComment>();
        var summaryParts = new List<string>();

        _logger.LogInformation(
            "Starting review for PR #{PrNumber} with {Count} analyzers registered.",
            context.PullRequestNumber, sortedAnalyzers.Count);

        foreach (var analyzer in sortedAnalyzers)
        {
            _logger.LogInformation("Executing analyzer [{Name}] (Order: {Order})...", analyzer.Name, analyzer.Order);

            try
            {
                var comments = await analyzer.AnalyzeAsync(context, cancellationToken);
                allComments.AddRange(comments);

                // Accumulate summaries from every analyzer so none are silently discarded
                if (!string.IsNullOrWhiteSpace(analyzer.Summary))
                {
                    summaryParts.Add(analyzer.Summary);
                }

                _logger.LogInformation("Analyzer [{Name}] finished with {Count} finding(s).", analyzer.Name, comments.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analyzer [{Name}] threw an unhandled exception.", analyzer.Name);
                allComments.Add(new ReviewComment
                {
                    FilePath = "Analyzer Internal",
                    Line = 0,
                    Body = $"⚠️ Analyzer `{analyzer.Name}` failed to complete: {ex.Message}",
                    Severity = AnalyzerSeverity.Error,
                    AnalyzerName = analyzer.Name
                });
            }
        }

        var reviewResult = new ReviewResult
        {
            Comments = allComments,
            SummaryMarkdown = summaryParts.Count > 0 ? string.Join("\n\n---\n\n", summaryParts) : null
        };

        if (context.PriorDefects.Count > 0)
        {
            reviewResult.ResolvedDefects.AddRange(context.PriorDefects.Where(d => d.IsResolved));
            reviewResult.ActivePriorDefects.AddRange(context.PriorDefects.Where(d => !d.IsResolved));

            // Ensure any findings that were confirmed resolved are not retained in Comments
            reviewResult.Comments.RemoveAll(c =>
                reviewResult.ResolvedDefects.Any(r =>
                    r.FilePath.Equals(c.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(r.Line - c.Line) <= 3));
        }

        return reviewResult;
    }
}
