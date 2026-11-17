namespace PR_Bot.Analyzers.Gemini;

/// <summary>
/// Configuration settings for the Google Gemini AI code review analyzer.
/// </summary>
public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    /// <summary>
    /// Google AI Studio API key.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Primary model name to use (default: gemini-flash-lite-latest).
    /// </summary>
    public string Model { get; set; } = "gemini-flash-lite-latest";

    /// <summary>
    /// Fallback free-tier models to try if the primary model returns 503 Service Unavailable, 429, or 404.
    /// </summary>
    public string[] FallbackModels { get; set; } =
    [
        "gemini-flash-lite-latest",
        "gemini-3.5-flash-lite",
        "gemini-3.6-flash",
        "gemini-3.8-flash"
    ];

    /// <summary>
    /// Sampling temperature (lower = more deterministic for code analysis).
    /// </summary>
    public float Temperature { get; set; } = 0.2f;

    /// <summary>
    /// Maximum tokens to return per chunk request.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 8192;

    /// <summary>
    /// Maximum character length per diff chunk sent to Gemini.
    /// </summary>
    public int MaxChunkCharacters { get; set; } = 15000;
}
