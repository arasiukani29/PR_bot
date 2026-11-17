namespace PR_Bot.Action;

using PR_Bot.Analyzers.Gemini;

/// <summary>
/// Reads and validates inputs provided by GitHub Actions environment variables.
/// </summary>
public sealed class ActionInputs
{
    public string GitHubToken { get; }
    public string Owner { get; }
    public string Repo { get; }
    public int PullRequestNumber { get; }
    public string Title { get; }
    public string Author { get; }
    public string BaseBranch { get; }
    public string HeadBranch { get; }
    public string HeadSha { get; }
    public string? GeminiApiKey { get; }
    public string GeminiModel { get; }
    public bool FailOnErrors { get; }

    public ActionInputs()
    {
        GitHubToken = GetRequired("GITHUB_TOKEN");

        var repository = GetRequired("GITHUB_REPOSITORY"); // Format: "owner/repo"
        var parts = repository.Split('/');
        if (parts.Length != 2)
        {
            throw new InvalidOperationException($"Invalid GITHUB_REPOSITORY format: '{repository}'. Expected 'owner/repo'.");
        }

        Owner = parts[0];
        Repo = parts[1];

        var prNumberStr = GetRequired("PR_NUMBER");
        if (!int.TryParse(prNumberStr, out var prNumber))
        {
            throw new InvalidOperationException($"Invalid PR_NUMBER value: '{prNumberStr}'. Must be an integer.");
        }
        PullRequestNumber = prNumber;

        Title = Environment.GetEnvironmentVariable("PR_TITLE") ?? $"PR #{prNumber}";
        Author = Environment.GetEnvironmentVariable("PR_AUTHOR") ?? "unknown";
        BaseBranch = Environment.GetEnvironmentVariable("PR_BASE") ?? "main";
        HeadBranch = Environment.GetEnvironmentVariable("PR_HEAD") ?? "feature";
        HeadSha = Environment.GetEnvironmentVariable("PR_HEAD_SHA") ?? "HEAD";

        GeminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var envModel = Environment.GetEnvironmentVariable("GEMINI_MODEL");
        GeminiModel = !string.IsNullOrWhiteSpace(envModel) ? envModel : GeminiOptions.DefaultModel;

        var failOnErrStr = Environment.GetEnvironmentVariable("FAIL_ON_ERRORS") ?? "false";
        FailOnErrors = bool.TryParse(failOnErrStr, out var fail) && fail;
    }

    private static string GetRequired(string variableName)
    {
        return Environment.GetEnvironmentVariable(variableName)
            ?? throw new InvalidOperationException($"Required environment variable '{variableName}' was not provided.");
    }
}
