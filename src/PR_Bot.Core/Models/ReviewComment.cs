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
}
