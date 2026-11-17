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
    /// The single source of truth default model name.
    /// Changing this value updates the default model everywhere across the bot.
    /// Model identifiers verified against: https://ai.google.dev/gemini-api/docs/models
    /// Last verified: November 2026 — gemini-3.8-flash is current recommended free-tier model.
    /// </summary>
    public const string DefaultModel = "gemini-flash-lite-latest";

    /// <summary>
    /// Primary model name to use.
    /// </summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>
    /// Fallback models to try if the primary model returns 503 Service Unavailable, 429, or 404.
    /// All are free-tier eligible on Google AI Studio. Listed best → oldest as fallback chain.
    /// - gemini-3.8-flash:        Current recommended workhorse, free tier, large context.
    /// - gemini-flash-lite-latest: Auto-updating alias — always resolves to newest Flash-Lite release.
    /// - gemini-3.6-flash:        Previous generation, free tier, still active.
    /// - gemini-3.5-flash-lite:   Lightweight free-tier option.
    /// - gemini-2.5-flash:        Older stable generation, free tier (may 404 on new projects).
    /// </summary>
    public string[] FallbackModels { get; set; } =
    [
        "gemini-flash-lite-latest",      // Auto-updating alias — always newest Flash-Lite (primary)
        "gemini-3.8-flash",              // Current pinned workhorse, free tier
        "gemini-3.6-flash",              // Previous gen, free tier, still active
        "gemini-3.5-flash-lite",         // Lightweight free-tier option
        "gemini-2.5-flash"               // Older stable safety net
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
