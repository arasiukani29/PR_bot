namespace PR_Bot.Core.Models;

/// <summary>
/// Encapsulates all data and changed files for a Pull Request under review.
/// </summary>
public sealed class PullRequestContext
{
    public required string Owner { get; init; }
    public required string Repo { get; init; }
    public required int PullRequestNumber { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }
    public required string BaseBranch { get; init; }
    public required string HeadBranch { get; init; }
    public required string HeadSha { get; init; }
    public List<PullRequestFileChange> ChangedFiles { get; set; } = [];
}

/// <summary>
/// Represents a file modified in the pull request with patch diff.
/// </summary>
public sealed class PullRequestFileChange
{
    public required string FileName { get; init; }
    public required string Status { get; init; } // added, modified, removed, renamed
    public required int Additions { get; init; }
    public required int Deletions { get; init; }
    public required int Changes { get; init; }
    public string? Patch { get; init; }
}
