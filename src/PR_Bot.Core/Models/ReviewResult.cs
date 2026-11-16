namespace PR_Bot.Core.Models;

/// <summary>
/// Aggregates all analyzer findings, summary remarks, and health status for a PR.
/// </summary>
public sealed class ReviewResult
{
    public List<ReviewComment> Comments { get; init; } = [];
    public string? SummaryMarkdown { get; set; }
    public bool HasErrors => Comments.Any(c => c.Severity == AnalyzerSeverity.Error);
    public bool HasWarnings => Comments.Any(c => c.Severity == AnalyzerSeverity.Warning);
}
