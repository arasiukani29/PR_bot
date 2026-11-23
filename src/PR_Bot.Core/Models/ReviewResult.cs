namespace PR_Bot.Core.Models;

/// <summary>
/// Aggregates all analyzer findings, summary remarks, and health status for a PR.
/// </summary>
public sealed class ReviewResult
{
    public List<ReviewComment> Comments { get; init; } = [];
    public string? SummaryMarkdown { get; set; }
    public bool HasErrors => Comments.Any(c => c.Severity >= AnalyzerSeverity.Error);
    public bool HasWarnings => Comments.Any(c => c.Severity == AnalyzerSeverity.Warning);
    public bool HasDefectsAtOrAbove(AnalyzerSeverity minSeverity) => Comments.Any(c => c.Severity >= minSeverity);

    public int CriticalCount => Comments.Count(c => c.Severity == AnalyzerSeverity.Critical);
    public int HighCount => Comments.Count(c => c.Severity == AnalyzerSeverity.Error);
    public int MediumCount => Comments.Count(c => c.Severity == AnalyzerSeverity.Warning);
    public int LowCount => Comments.Count(c => c.Severity == AnalyzerSeverity.Info);

    /// <summary>
    /// Calculated code health score from 0 (critical defects) to 100 (clean code).
    /// Deductions: Critical (-30), High priority (-25), Medium priority (-10), Low priority (-3).
    /// </summary>
    public int HealthScore
    {
        get
        {
            int deduction = (CriticalCount * 30) + (HighCount * 25) + (MediumCount * 10) + (LowCount * 3);
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
    /// Formatted visual progress meter matching the quality grade colors:
    /// Grade A (🟩), Grade B (🟨), Grade C (🟧), Grade F (🟥), remaining (⬛).
    /// </summary>
    public string HealthProgressBar
    {
        get
        {
            const int totalBlocks = 10;
            int filledBlocks = (int)Math.Round((HealthScore / 100.0) * totalBlocks);
            filledBlocks = Math.Clamp(filledBlocks, 0, totalBlocks);
            int emptyBlocks = totalBlocks - filledBlocks;

            string fillChar = HealthScore switch
            {
                >= 90 => "🟩",
                >= 75 => "🟨",
                >= 50 => "🟧",
                _ => "🟥"
            };
            const string emptyChar = "⬛";

            var bar = string.Concat(Enumerable.Repeat(fillChar, filledBlocks)) +
                      string.Concat(Enumerable.Repeat(emptyChar, emptyBlocks));

            return $"{bar} &nbsp; **{HealthScore}%**";
        }
    }
}
