namespace PR_Bot.Analyzers.Gemini;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

/// <summary>
/// Lightweight HTTP client for Google Gemini API.
/// </summary>
public sealed class GeminiApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<GeminiApiClient> _logger;

    public GeminiApiClient(HttpClient httpClient, ILogger<GeminiApiClient>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GeminiApiClient>.Instance;
    }

    public async Task<string?> GenerateJsonAsync(
        string apiKey,
        string model,
        string systemInstruction,
        string userPrompt,
        float temperature = 0.2f,
        int maxOutputTokens = 8192,
        CancellationToken cancellationToken = default)
    {
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        var requestBody = new
        {
            system_instruction = new
            {
                parts = new[] { new { text = systemInstruction } }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = userPrompt } }
                }
            },
            generationConfig = new
            {
                temperature,
                maxOutputTokens,
                responseMimeType = "application/json"
            }
        };

        var jsonPayload = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        _logger.LogDebug("Sending request to Gemini API ({Model})...", model);

        using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Gemini API call failed with status {StatusCode}: {Error}", response.StatusCode, errorBody);
            throw new HttpRequestException($"Gemini API error ({response.StatusCode}): {errorBody}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var firstCandidate = candidates[0];
            if (firstCandidate.TryGetProperty("content", out var candidateContent) &&
                candidateContent.TryGetProperty("parts", out var parts) &&
                parts.GetArrayLength() > 0)
            {
                var text = parts[0].GetProperty("text").GetString();
                return text;
            }
        }

        return null;
    }
}
