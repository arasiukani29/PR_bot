namespace PR_Bot.Core.Analyzers;

using System.Text.RegularExpressions;
using PR_Bot.Core.Models;

/// <summary>
/// Detects technical debt markers (TODO, FIXME, HACK, XXX) in added code.
/// </summary>
public sealed class TodoAnalyzer : IReviewAnalyzer
{
    private static readonly Regex HunkRegex = new(@"^@@\s+-\d+(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s+@@", RegexOptions.Compiled);
    private static readonly Regex MarkerRegex = new(@"(?i)\b(TODO|FIXME|HACK|XXX|WORKAROUND)\b(?:\s*[:\-]\s*(.*))?", RegexOptions.Compiled);

    public string Name => "TodoAnalyzer";
    public int Order => 20;

    public Task<IReadOnlyList<ReviewComment>> AnalyzeAsync(
        PullRequestContext context,
        CancellationToken cancellationToken = default)
    {
        var comments = new List<ReviewComment>();

        foreach (var file in context.ChangedFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Patch) || file.Status.Equals("removed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = file.Patch.Split('\n');
            int currentLine = 0;

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');

                var match = HunkRegex.Match(line);
                if (match.Success)
                {
                    currentLine = int.Parse(match.Groups[1].Value);
                    continue;
                }

                if (line.StartsWith('+') && !line.StartsWith("+++"))
                {
                    var content = line[1..];
                    var markerMatch = MarkerRegex.Match(content);

                    if (markerMatch.Success)
                    {
                        var marker = markerMatch.Groups[1].Value.ToUpperInvariant();
                        var note = markerMatch.Groups[2].Value.Trim();
                        var detail = string.IsNullOrEmpty(note) ? "Marker found" : $": \"{note}\"";

                        comments.Add(new ReviewComment
                        {
                            FilePath = file.FileName,
                            Line = currentLine,
                            Body = $"📝 **{marker}** reminder{detail}. Ensure this is tracked or resolved before merging.",
                            Severity = AnalyzerSeverity.Info,
                            AnalyzerName = Name
                        });
                    }

                    currentLine++;
                }
                else if (line.StartsWith('-') && !line.StartsWith("---"))
                {
                    // Deleted line
                }
                else if (line.StartsWith(' '))
                {
                    currentLine++;
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ReviewComment>>(comments);
    }
}
