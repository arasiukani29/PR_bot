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

    /// <summary>
    /// Calculated code health score from 0 (critical defects) to 100 (clean code).
    /// Deductions: High priority (-25), Medium priority (-10), Low priority (-3).
    /// </summary>
    public int HealthScore
    {
        get
        {
            int highCount = Comments.Count(c => c.Severity == AnalyzerSeverity.Error);
            int mediumCount = Comments.Count(c => c.Severity == AnalyzerSeverity.Warning);
            int lowCount = Comments.Count(c => c.Severity == AnalyzerSeverity.Info);

            int deduction = (highCount * 25) + (mediumCount * 10) + (lowCount * 3);
            return Math.Max(0, 100 - deduction);
        }
    }

    /// <summary>
    /// Letter grade evaluation corresponding to the health score.
    /// </summary>
    public string HealthGrade => HealthScore switch
    {
        >= 90 => "A (Excellent)",
        >= 75 => "B (Good)",
        >= 50 => "C (Needs Attention)",
        _ => "F (Critical Action Required)"
    };

    /// <summary>
    /// Formatted visual progress meter (e.g. [██████████░░░░░░░░░░] 50%).
    /// </summary>
    public string HealthProgressBar
    {
        get
        {
            const int totalBlocks = 20;
            int filledBlocks = (int)Math.Round((HealthScore / 100.0) * totalBlocks);
            filledBlocks = Math.Clamp(filledBlocks, 0, totalBlocks);
            int emptyBlocks = totalBlocks - filledBlocks;

            return $"[{new string('█', filledBlocks)}{new string('░', emptyBlocks)}] {HealthScore}%";
        }
    }
}
