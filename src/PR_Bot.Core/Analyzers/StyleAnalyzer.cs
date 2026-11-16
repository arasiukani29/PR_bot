namespace PR_Bot.Core.Analyzers;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PR_Bot.Core.Configuration;
using PR_Bot.Core.Models;

/// <summary>
/// Scans added lines in pull request diffs for code style and formatting issues.
/// </summary>
public sealed class StyleAnalyzer : IReviewAnalyzer
{
    private static readonly Regex HunkRegex = new(@"^@@\s+-\d+(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s+@@", RegexOptions.Compiled);

    private readonly PrBotOptions _options;

    public StyleAnalyzer(IOptions<PrBotOptions>? options = null)
    {
        _options = options?.Value ?? new PrBotOptions();
    }

    public string Name => "StyleAnalyzer";
    public int Order => 10;

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
            int diffPosition = 0;
            bool foundFirstHunk = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');

                var match = HunkRegex.Match(line);
                if (match.Success)
                {
                    foundFirstHunk = true;
                    currentLine = int.Parse(match.Groups[1].Value);
                    diffPosition++;
                    continue;
                }

                if (!foundFirstHunk) continue;
                diffPosition++;

                if (line.StartsWith('+') && !line.StartsWith("+++"))
                {
                    var content = line[1..];

                    // Check line length
                    if (content.Length > _options.MaxLineLength)
                    {
                        comments.Add(new ReviewComment
                        {
                            FilePath = file.FileName,
                            Line = currentLine,
                            DiffPosition = diffPosition,
                            Body = $"⚠️ Line exceeds {_options.MaxLineLength} characters ({content.Length} characters).",
                            Severity = AnalyzerSeverity.Warning,
                            AnalyzerName = Name
                        });
                    }

                    // Check trailing whitespace
                    if (content.Length > 0 && char.IsWhiteSpace(content[^1]))
                    {
                        comments.Add(new ReviewComment
                        {
                            FilePath = file.FileName,
                            Line = currentLine,
                            DiffPosition = diffPosition,
                            Body = "💡 Trailing whitespace detected.",
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
                    // Context line
                    currentLine++;
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ReviewComment>>(comments);
    }
}
