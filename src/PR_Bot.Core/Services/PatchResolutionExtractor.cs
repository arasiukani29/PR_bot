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

        // 2. Format the hunk lines, ensuring hunk header and changed lines around targetLine are included
        var sb = new StringBuilder();
        int maxLinesToInclude = 20;

        var displayLines = matchedHunk.Lines.Count <= maxLinesToInclude
            ? matchedHunk.Lines.Select(l => l.Text).ToList()
            : SelectRelevantHunkLines(matchedHunk, targetLine, maxLinesToInclude);

        // Always prepend hunk header if not already present in displayLines
        if (displayLines.Count > 0 && !displayLines[0].StartsWith("@@"))
        {
            sb.AppendLine(matchedHunk.Header);
        }

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
                currentHunk.Lines.Add((line, startLine));
                continue;
            }

            if (currentHunk == null)
            {
                continue;
            }

            if (line.StartsWith('+') && !line.StartsWith("+++"))
            {
                currentHunk.Lines.Add((line, currentLine));
                currentLine++;
            }
            else if (line.StartsWith('-') && !line.StartsWith("---"))
            {
                currentHunk.Lines.Add((line, currentLine));
            }
            else
            {
                currentHunk.Lines.Add((line, currentLine));
                if (line.StartsWith(' '))
                {
                    currentLine++;
                }
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
        // 1. Find the changed line (+ or -) closest to targetLine
        int targetIndex = -1;
        int minDistance = int.MaxValue;

        for (int i = 0; i < hunk.Lines.Count; i++)
        {
            var (text, lineNum) = hunk.Lines[i];
            if (text.StartsWith('+') || text.StartsWith('-'))
            {
                int dist = Math.Abs(lineNum - targetLine);
                if (dist < minDistance)
                {
                    minDistance = dist;
                    targetIndex = i;
                }
            }
        }

        // 2. If no changed line was found, pick line closest to targetLine
        if (targetIndex < 0)
        {
            for (int i = 0; i < hunk.Lines.Count; i++)
            {
                var (_, lineNum) = hunk.Lines[i];
                int dist = Math.Abs(lineNum - targetLine);
                if (dist < minDistance)
                {
                    minDistance = dist;
                    targetIndex = i;
                }
            }
        }

        if (targetIndex < 0)
        {
            targetIndex = 0;
        }

        // Leave 1 slot for header if needed
        int contentSlots = maxLines - 1;
        int half = contentSlots / 2;
        int start = Math.Max(1, targetIndex - half); // skip index 0 which is header
        int count = Math.Min(contentSlots, hunk.Lines.Count - start);

        var result = new List<string> { hunk.Header };
        for (int i = start; i < start + count; i++)
        {
            if (i < hunk.Lines.Count && hunk.Lines[i].Text != hunk.Header)
            {
                result.Add(hunk.Lines[i].Text);
            }
        }

        return result;
    }

    private sealed class HunkData
    {
        public string Header { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public List<(string Text, int LineNumber)> Lines { get; } = new();
    }
}
