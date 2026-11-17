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
        string? aiSummary = null;

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

                if (!string.IsNullOrWhiteSpace(analyzer.Summary))
                {
                    aiSummary = analyzer.Summary;
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

        return new ReviewResult
        {
            Comments = allComments,
            SummaryMarkdown = aiSummary
        };
    }
}
