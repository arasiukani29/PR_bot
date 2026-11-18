namespace PR_Bot.Core.Models;

/// <summary>
/// Indicates the severity level of a review finding.
/// Higher numerical values indicate higher severity.
/// </summary>
public enum AnalyzerSeverity
{
    Info = 0,       // Low / Suggestion
    Low = 0,
    Warning = 1,    // Medium
    Medium = 1,
    Error = 2,      // High
    High = 2,
    Critical = 3    // Critical Blocker
}
