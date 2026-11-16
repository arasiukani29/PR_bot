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
    /// Model name to use (default: gemini-2.5-flash).
    /// </summary>
    public string Model { get; set; } = "gemini-2.5-flash";

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
