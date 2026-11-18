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
        "gemini-flash-lite-latest",      // Primary stable model (high availability)
        "gemini-2.5-flash",              // High availability stable fallback
        "gemini-3.8-flash",              // Deep reasoning fallback (tried when capacity permits)
        "gemini-3.6-flash",              // Previous generation fallback
        "gemini-3.5-flash-lite"          // Lightweight fallback
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
    /// Default is 500,000 chars (~125k tokens), allowing all changed files to be sent in a single API call
    /// within Gemini's 1M+ token context window.
    /// </summary>
    public int MaxChunkCharacters { get; set; } = 500_000;

    /// <summary>
    /// Maximum number of changed files to bundle into a single AI review chunk.
    /// Default is 50, allowing all changed files in the PR to be reviewed together in one unified API call
    /// with full cross-file context awareness.
    /// </summary>
    public int MaxFilesPerChunk { get; set; } = 50;

    /// <summary>
    /// Maximum line count in a file to include the full file as context.
    /// Files exceeding this line count will switch to windowed context (header + surrounding hunk lines).
    /// </summary>
    public int MaxFullFileLines { get; set; } = 600;

    /// <summary>
    /// Number of context lines before and after each changed hunk when a file is too large for full file context.
    /// </summary>
    public int SurroundingContextLines { get; set; } = 25;
}
