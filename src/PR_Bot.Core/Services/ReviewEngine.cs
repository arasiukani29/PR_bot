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
            // Detect regressions: if an analyzer reported a defect on a file & line that was
            // previously resolved in an earlier commit, the defect has REGRESSED in the latest code!
            foreach (var comment in reviewResult.Comments)
            {
                var regressed = context.PriorDefects.FirstOrDefault(p =>
                    p.WasPreviouslyResolved &&
                    p.FilePath.Equals(comment.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(p.Line - comment.Line) <= 3);

                if (regressed != null)
                {
                    _logger.LogWarning("Regression detected on {File}:{Line}! Marking prior defect #{CommentId} as un-resolved.",
                        regressed.FilePath, regressed.Line, regressed.CommentId);
                    regressed.IsResolved = false;
                    regressed.ResolutionExplanation = null;
                }
            }

            // Only remove duplicates if the defect was newly resolved by AI in this run
            var newlyResolved = context.PriorDefects.Where(p => !p.WasPreviouslyResolved && p.IsResolved).ToList();
            if (newlyResolved.Count > 0)
            {
                reviewResult.Comments.RemoveAll(c =>
                    newlyResolved.Any(r =>
                        r.FilePath.Equals(c.FilePath, StringComparison.OrdinalIgnoreCase) &&
                        Math.Abs(c.Line - c.Line) <= 3));
            }

            reviewResult.ResolvedDefects.AddRange(context.PriorDefects.Where(d => d.IsResolved));

            // Deduplicate ActivePriorDefects against Comments so defects are never double counted
            reviewResult.ActivePriorDefects.AddRange(
                context.PriorDefects.Where(d =>
                    !d.IsResolved &&
                    !reviewResult.Comments.Any(c =>
                        c.FilePath.Equals(d.FilePath, StringComparison.OrdinalIgnoreCase) &&
                        Math.Abs(c.Line - d.Line) <= 3)));
        }

        return reviewResult;
    }
}
