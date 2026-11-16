namespace PR_Bot.Core.Models;

/// <summary>
/// Represents a single finding or suggestion on a file in the Pull Request.
/// </summary>
public sealed class ReviewComment
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string Body { get; init; }
    public AnalyzerSeverity Severity { get; init; } = AnalyzerSeverity.Warning;
    public required string AnalyzerName { get; init; }
}
