namespace PR_Bot.Core.Models;

/// <summary>
/// Represents a previously reported bot finding on a pull request.
/// Used for tracking defect persistence and AI reconciliation across commits.
/// </summary>
public sealed class PriorReviewDefect
{
    /// <summary>
    /// GitHub Review Comment ID.
    /// </summary>
    public long CommentId { get; init; }

    /// <summary>
    /// File path where the finding was reported.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// Line number where the finding was reported.
    /// </summary>
    public int Line { get; init; }

    /// <summary>
    /// Title of the finding.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Severity classification.
    /// </summary>
    public AnalyzerSeverity Severity { get; init; } = AnalyzerSeverity.Warning;

    /// <summary>
    /// Original comment body.
    /// </summary>
    public required string OriginalBody { get; init; }

    /// <summary>
    /// Whether this finding has been marked resolved.
    /// </summary>
    public bool IsResolved { get; set; }

    /// <summary>
    /// Whether this finding was already marked resolved on GitHub prior to this review run.
    /// Used to distinguish fresh resolutions from regressions when code is modified again.
    /// </summary>
    public bool WasPreviouslyResolved { get; set; }

    /// <summary>
    /// Optional explanation from AI or patch analysis of how the defect was resolved.
    /// </summary>
    public string? ResolutionExplanation { get; set; }
}
