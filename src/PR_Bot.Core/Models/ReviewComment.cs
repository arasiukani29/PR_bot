namespace PR_Bot.Core.Models;

/// <summary>
/// Represents a single finding or suggestion on a file in the Pull Request.
/// </summary>
public sealed class ReviewComment
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }

    /// <summary>
    /// The line position within the git diff patch (required by GitHub for inline review comments).
    /// </summary>
    public int? DiffPosition { get; init; }

    public required string Body { get; init; }
    public AnalyzerSeverity Severity { get; init; } = AnalyzerSeverity.Warning;
    public required string AnalyzerName { get; init; }

    /// <summary>
    /// Short, descriptive title for the finding (e.g., "Logging Sensitive Information (Plaintext Password PII)").
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Review category (e.g., "Security", "Performance", "Reliability", "Input Validation", "Error Handling").
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Business/system impact explanation — why this issue matters.
    /// </summary>
    public string? WhyItMatters { get; init; }

    /// <summary>
    /// Concrete replacement code for GitHub's suggestion block.
    /// When present, this is rendered inside a ```suggestion fenced block so GitHub shows the red/green inline diff.
    /// Must contain only the replacement source lines (no fences, no explanation).
    /// </summary>
    public string? SuggestedFix { get; init; }
}
