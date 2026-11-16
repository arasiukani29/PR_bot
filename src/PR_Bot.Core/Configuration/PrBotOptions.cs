namespace PR_Bot.Core.Configuration;

/// <summary>
/// General configuration options for the PR bot.
/// </summary>
public sealed class PrBotOptions
{
    public const string SectionName = "PrBot";

    public int MaxFilesPerPr { get; set; } = 50;
    public int MaxLineLength { get; set; } = 120;
    public int MaxTotalChanges { get; set; } = 1000;
    public int MaxSingleFileAdditions { get; set; } = 500;

    /// <summary>
    /// File glob patterns to skip during analysis.
    /// </summary>
    public string[] IgnoredFilePatterns { get; set; } =
    [
        "*.lock",
        "*.min.js",
        "*.min.css",
        "*.g.cs",
        "*.Designer.cs",
        "package-lock.json",
        "yarn.lock",
        "pnpm-lock.yaml",
        "*.png",
        "*.jpg",
        "*.jpeg",
        "*.gif",
        "*.ico",
        "*.svg",
        "*.woff",
        "*.woff2"
    ];
}
