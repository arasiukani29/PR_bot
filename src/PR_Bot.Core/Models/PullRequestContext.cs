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
    public string? Description { get; set; }
    public required string BaseBranch { get; init; }
    public required string HeadBranch { get; init; }
    public required string HeadSha { get; init; }
    public ReviewEventMode ReviewEventMode { get; init; } = ReviewEventMode.Dynamic;
    public List<PullRequestFileChange> ChangedFiles { get; set; } = [];

    /// <summary>
    /// Existing bot findings retrieved from previous PR reviews.
    /// Used by analyzers to reconcile resolved items and track persistent open defects.
    /// </summary>
    public List<PriorReviewDefect> PriorDefects { get; set; } = [];
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

    /// <summary>
    /// The complete file content at the PR head commit (used for AI background context).
    /// </summary>
    public string? FullContent { get; set; }

    private static readonly System.Text.RegularExpressions.Regex HunkRegex =
        new(@"^@@\s+-\d+(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s+@@", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Returns the list of line numbers in the new file that were added or modified in the patch diff.
    /// </summary>
    public List<int> GetAddedOrModifiedLines()
    {
        var addedLines = new List<int>();
        if (string.IsNullOrWhiteSpace(Patch)) return addedLines;

        var lines = Patch.Split('\n');
        int currentLine = 0;
        bool inHunk = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var match = HunkRegex.Match(line);
            if (match.Success)
            {
                inHunk = true;
                currentLine = int.Parse(match.Groups[1].Value);
                continue;
            }

            if (!inHunk) continue;

            if (line.StartsWith('+') && !line.StartsWith("+++"))
            {
                addedLines.Add(currentLine);
                currentLine++;
            }
            else if (line.StartsWith(' '))
            {
                currentLine++;
            }
        }

        return addedLines;
    }
}
