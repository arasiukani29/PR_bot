namespace PR_Bot.Analyzers.Gemini;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PR_Bot.Analyzers.Gemini.Prompts;
using PR_Bot.Core.Analyzers;
using PR_Bot.Core.Models;

/// <summary>
/// AI-powered code review analyzer powered by Google Gemini.
/// </summary>
public sealed class GeminiReviewAnalyzer : IReviewAnalyzer
{
    private static readonly Regex HunkRegex = new(@"^@@\s+-\d+(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s+@@", RegexOptions.Compiled);

    private readonly GeminiApiClient _client;


    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiReviewAnalyzer> _logger;

    public GeminiReviewAnalyzer(
        GeminiApiClient client,
        IOptions<GeminiOptions>? options = null,
        ILogger<GeminiReviewAnalyzer>? logger = null)
    {
        _client = client;
        _options = options?.Value ?? new GeminiOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GeminiReviewAnalyzer>.Instance;
    }

    public string Name => "GeminiAI";
    public int Order => 50; // Runs after rule-based checks
    public string? Summary { get; private set; }

    public async Task<IReadOnlyList<ReviewComment>> AnalyzeAsync(
        PullRequestContext context,
        CancellationToken cancellationToken = default)
    {
        Summary = null;
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("Gemini API key is not configured. Skipping AI review.");
            return [];
        }

        var chunks = BuildDiffChunks(context, _options.MaxChunkCharacters);
        if (chunks.Count == 0)
        {
            _logger.LogInformation("No modified files with patch diffs to review.");
            return [];
        }

        var candidateModels = new List<string> { _options.Model };
        if (_options.FallbackModels != null)
        {
            candidateModels.AddRange(_options.FallbackModels);
        }
        var modelsToTry = candidateModels
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "Submitting {ChunkCount} diff chunk(s) to Gemini. Primary model: {Model}, Fallbacks: [{Fallbacks}]",
            chunks.Count, _options.Model, string.Join(", ", modelsToTry.Skip(1)));

        var comments = new List<ReviewComment>();
        var summaries = new List<string>();
        string currentWorkingModel = modelsToTry[0];

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            _logger.LogInformation("Analyzing chunk {Index}/{Total} ({Length} chars)...", i + 1, chunks.Count, chunk.Length);

            var userPrompt = CodeReviewPrompt.BuildUserMessage(context.Title, context.Author, chunk);
            string? jsonResponse = null;

            var modelQueue = new List<string> { currentWorkingModel };
            modelQueue.AddRange(modelsToTry.Where(m => !m.Equals(currentWorkingModel, StringComparison.OrdinalIgnoreCase)));

            foreach (var model in modelQueue)
            {
                try
                {
                    _logger.LogInformation("Attempting review with Gemini model '{Model}'...", model);

                    jsonResponse = await _client.GenerateJsonAsync(
                        apiKey: _options.ApiKey,
                        model: model,
                        systemInstruction: CodeReviewPrompt.SystemInstruction,
                        userPrompt: userPrompt,
                        temperature: _options.Temperature,
                        maxOutputTokens: _options.MaxOutputTokens,
                        cancellationToken: cancellationToken);

                    if (!string.IsNullOrWhiteSpace(jsonResponse))
                    {
                        currentWorkingModel = model;
                        _logger.LogInformation("Successfully received review from model '{Model}'.", model);
                        break;
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Gemini model '{Model}' returned {StatusCode}. Trying fallback model in 1.5s...", model, ex.StatusCode);
                    await Task.Delay(1500, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Gemini model '{Model}' failed. Trying next fallback model...", model);
                }
            }

            if (!string.IsNullOrWhiteSpace(jsonResponse))
            {
                var (parsedComments, summary) = ParseGeminiResponse(jsonResponse, context);
                comments.AddRange(parsedComments);

                if (!string.IsNullOrWhiteSpace(summary))
                {
                    summaries.Add(summary);
                }
            }
            else
            {
                _logger.LogError("Failed to get review response from any Gemini model for chunk {Index}.", i + 1);
            }
        }

        if (summaries.Count > 0)
        {
            Summary = string.Join("\n\n", summaries);
        }

        var sortedComments = comments
            .OrderByDescending(c => c.Severity) // High (Error) -> Medium (Warning) -> Low (Info)
            .ThenBy(c => c.FilePath)
            .ThenBy(c => c.Line)
            .ToList();

        _logger.LogInformation("Gemini AI review finished. Generated {Count} issue(s).", sortedComments.Count);
        return sortedComments;
    }

    public static List<string> BuildDiffChunks(PullRequestContext context, int maxChunkSize)
    {
        var chunks = new List<string>();
        var currentChunk = new StringBuilder();

        foreach (var file in context.ChangedFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Patch) || file.Status.Equals("removed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileHeader = $"--- a/{file.FileName}\n+++ b/{file.FileName}\n{file.Patch}\n\n";

            if (currentChunk.Length + fileHeader.Length > maxChunkSize && currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString());
                currentChunk.Clear();
            }

            currentChunk.Append(fileHeader);
        }

        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString());
        }

        return chunks;
    }

    public (List<ReviewComment> Comments, string? Summary) ParseGeminiResponse(string json, PullRequestContext? context = null)
    {
        var comments = new List<ReviewComment>();
        string? summary = null;

        try
        {
            var cleanJson = json.Trim();
            if (cleanJson.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                cleanJson = cleanJson[7..];
            }
            else if (cleanJson.StartsWith("```"))
            {
                cleanJson = cleanJson[3..];
            }

            if (cleanJson.EndsWith("```"))
            {
                cleanJson = cleanJson[..^3];
            }

            cleanJson = cleanJson.Trim();

            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("summary", out var summaryProp) && summaryProp.ValueKind == JsonValueKind.String)
            {
                summary = summaryProp.GetString();
            }

            if (root.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
            {
                foreach (var finding in findings.EnumerateArray())
                {
                    var file = finding.TryGetProperty("file", out var fp) ? fp.GetString() ?? "unknown" : "unknown";
                    var line = finding.TryGetProperty("line", out var lp) && lp.TryGetInt32(out var l) ? l : 1;
                    var message = finding.TryGetProperty("message", out var mp) ? mp.GetString() ?? "" : "";

                    var priorityStr = finding.TryGetProperty("priority", out var pp) ? pp.GetString() :
                                      finding.TryGetProperty("severity", out var sp) ? sp.GetString() : "medium";

                    var severity = (priorityStr?.ToLowerInvariant()) switch
                    {
                        "high" or "error" or "critical" => AnalyzerSeverity.Error,
                        "low" or "info" => AnalyzerSeverity.Info,
                        _ => AnalyzerSeverity.Warning
                    };

                    var priorityBadge = severity switch
                    {
                        AnalyzerSeverity.Error => "🔴 **[HIGH PRIORITY]**",
                        AnalyzerSeverity.Warning => "🟡 **[MEDIUM PRIORITY]**",
                        _ => "🟢 **[LOW PRIORITY]**"
                    };

                    int? diffPosition = null;
                    if (context != null)
                    {
                        var matchingFile = context.ChangedFiles.FirstOrDefault(f =>
                            f.FileName.Equals(file, StringComparison.OrdinalIgnoreCase) ||
                            f.FileName.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith("/" + f.FileName, StringComparison.OrdinalIgnoreCase));

                        if (matchingFile != null)
                        {
                            file = matchingFile.FileName;
                            diffPosition = FindDiffPosition(matchingFile.Patch, line);
                        }
                    }

                    comments.Add(new ReviewComment
                    {
                        FilePath = file,
                        Line = line,
                        DiffPosition = diffPosition,
                        Body = $"🤖 **Gemini AI Review** {priorityBadge}\n\n{message}",
                        Severity = severity,
                        AnalyzerName = Name
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse JSON response from Gemini: {RawJson}", json);
        }

        return (comments, summary);
    }

    public static int? FindDiffPosition(string? patch, int targetLine)
    {
        if (string.IsNullOrWhiteSpace(patch)) return null;

        var lines = patch.Split('\n');
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
                if (currentLine == targetLine)
                {
                    return diffPosition;
                }
                currentLine++;
            }
            else if (line.StartsWith('-') && !line.StartsWith("---"))
            {
                // Deleted line in diff
            }
            else if (line.StartsWith(' '))
            {
                if (currentLine == targetLine)
                {
                    return diffPosition;
                }
                currentLine++;
            }
        }

        return null;
    }
}
