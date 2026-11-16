namespace PR_Bot.Analyzers.Gemini;

using System.Text;
using System.Text.Json;
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

    public async Task<IReadOnlyList<ReviewComment>> AnalyzeAsync(
        PullRequestContext context,
        CancellationToken cancellationToken = default)
    {
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

        _logger.LogInformation("Submitting {ChunkCount} diff chunk(s) to Gemini ({Model})...", chunks.Count, _options.Model);

        var comments = new List<ReviewComment>();
        var summaries = new List<string>();

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            _logger.LogInformation("Analyzing chunk {Index}/{Total} ({Length} chars)...", i + 1, chunks.Count, chunk.Length);

            try
            {
                var userPrompt = CodeReviewPrompt.BuildUserMessage(context.Title, context.Author, chunk);

                var jsonResponse = await _client.GenerateJsonAsync(
                    apiKey: _options.ApiKey,
                    model: _options.Model,
                    systemInstruction: CodeReviewPrompt.SystemInstruction,
                    userPrompt: userPrompt,
                    temperature: _options.Temperature,
                    maxOutputTokens: _options.MaxOutputTokens,
                    cancellationToken: cancellationToken);

                if (!string.IsNullOrWhiteSpace(jsonResponse))
                {
                    var (parsedComments, summary) = ParseGeminiResponse(jsonResponse);
                    comments.AddRange(parsedComments);

                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        summaries.Add(summary);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze diff chunk {Index} with Gemini.", i + 1);
            }
        }

        _logger.LogInformation("Gemini AI review finished. Generated {Count} issue(s).", comments.Count);
        return comments;
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

    public (List<ReviewComment> Comments, string? Summary) ParseGeminiResponse(string json)
    {
        var comments = new List<ReviewComment>();
        string? summary = null;

        try
        {
            // Sanitize potential markdown code fences: ```json ... ```
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
                    var severityStr = finding.TryGetProperty("severity", out var sp) ? sp.GetString() : "warning";

                    var severity = (severityStr?.ToLowerInvariant()) switch
                    {
                        "error" => AnalyzerSeverity.Error,
                        "info" => AnalyzerSeverity.Info,
                        _ => AnalyzerSeverity.Warning
                    };

                    comments.Add(new ReviewComment
                    {
                        FilePath = file,
                        Line = line,
                        Body = $"🤖 **Gemini AI:** {message}",
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
}
