namespace PR_Bot.Core.Models;

/// <summary>
/// Specifies how PR review verdicts map to GitHub Review Events.
/// </summary>
public enum ReviewEventMode
{
    /// <summary>
    /// Critical or High defects submit RequestChanges; clean PRs submit Approve; Medium/Low submit Comment.
    /// </summary>
    Dynamic,

    /// <summary>
    /// Critical, High, or Medium defects submit RequestChanges; clean PRs submit Approve.
    /// </summary>
    Strict,

    /// <summary>
    /// Always submits an advisory Comment review (legacy behavior, never blocks merge).
    /// </summary>
    CommentOnly
}
