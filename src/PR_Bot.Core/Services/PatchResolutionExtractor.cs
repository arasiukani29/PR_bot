namespace PR_Bot.Core.Services;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Extracts code diff snippets showing changes that resolved an issue on a specific line.
/// </summary>
public sealed class PatchResolutionExtractor : IPatchResolutionExtractor
{
    private static readonly Regex HunkHeaderRegex = new(
        @"^@@\s+-\d+(?:,\d+)?\s+\+(\d+)(?:,(\d+))?\s+@@",
        RegexOptions.Compiled);

    public string? ExtractResolutionDiff(string? patch, int targetLine)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return null;
        }

        var lines = patch.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var hunks = ParseHunks(lines);

        if (hunks.Count == 0)
        {
            return null;
        }

        // 1. Find the hunk that directly encompasses or is closest to targetLine
        HunkData? matchedHunk = null;
        int minDistance = int.MaxValue;

        foreach (var hunk in hunks)
        {
            if (targetLine >= hunk.StartLine && targetLine <= hunk.EndLine)
            {
                matchedHunk = hunk;
                break;
            }

            // Calculate distance to this hunk's boundary
            int distance = targetLine < hunk.StartLine
                ? hunk.StartLine - targetLine
                : targetLine - hunk.EndLine;

            if (distance < minDistance)
            {
                minDistance = distance;
                matchedHunk = hunk;
            }
        }

        if (matchedHunk == null)
        {
            return null;
        }

        // 2. Format the hunk lines, emphasizing added (+) and removed (-) changes
        var sb = new StringBuilder();
        var hunkLines = matchedHunk.Lines;

        // Take up to 20 lines of the relevant hunk to keep the comment readable
        int maxLinesToInclude = 20;
        var displayLines = hunkLines.Count <= maxLinesToInclude
            ? hunkLines
            : SelectRelevantHunkLines(matchedHunk, targetLine, maxLinesToInclude);

        foreach (var l in displayLines)
        {
            sb.AppendLine(l);
        }

        var result = sb.ToString().TrimEnd();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static List<HunkData> ParseHunks(string[] lines)
    {
        var hunks = new List<HunkData>();
        HunkData? currentHunk = null;
        int currentLine = 0;

        foreach (var line in lines)
        {
            var match = HunkHeaderRegex.Match(line);
            if (match.Success)
            {
                if (currentHunk != null)
                {
                    currentHunk.EndLine = Math.Max(currentHunk.StartLine, currentLine - 1);
                    hunks.Add(currentHunk);
                }

                int startLine = int.Parse(match.Groups[1].Value);
                currentLine = startLine;
                currentHunk = new HunkData
                {
                    Header = line,
                    StartLine = startLine,
                    EndLine = startLine
                };
                currentHunk.Lines.Add(line);
                continue;
            }

            if (currentHunk == null)
            {
                continue;
            }

            currentHunk.Lines.Add(line);

            if (line.StartsWith('+') && !line.StartsWith("+++"))
            {
                currentLine++;
            }
            else if (line.StartsWith(' '))
            {
                currentLine++;
            }
        }

        if (currentHunk != null)
        {
            currentHunk.EndLine = Math.Max(currentHunk.StartLine, currentLine - 1);
            hunks.Add(currentHunk);
        }

        return hunks;
    }

    private static List<string> SelectRelevantHunkLines(HunkData hunk, int targetLine, int maxLines)
    {
        // Prioritize lines with changes (+ or -) and immediate context
        int changeIndex = hunk.Lines.FindIndex(l => l.StartsWith('+') || l.StartsWith('-'));
        if (changeIndex < 0)
        {
            changeIndex = 0;
        }

        int half = maxLines / 2;
        int start = Math.Max(0, changeIndex - half);
        int count = Math.Min(maxLines, hunk.Lines.Count - start);

        return hunk.Lines.GetRange(start, count);
    }

    private sealed class HunkData
    {
        public string Header { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public List<string> Lines { get; } = new();
    }
}
