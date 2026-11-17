using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Octokit;
using PR_Bot.Action;
using PR_Bot.Analyzers.Gemini.Extensions;
using PR_Bot.Core.Extensions;
using PR_Bot.Core.Models;
using PR_Bot.Core.Services;

Console.WriteLine("==================================================");
Console.WriteLine("🚀 Universal PR Review Bot (C# / GitHub Action)");
Console.WriteLine("==================================================");

try
{
    // 1. Parse action inputs from environment
    var inputs = new ActionInputs();
    Console.WriteLine($"Repository: {inputs.Owner}/{inputs.Repo}");
    Console.WriteLine($"PR Number:  #{inputs.PullRequestNumber} ({inputs.Title})");
    Console.WriteLine($"Author:     @{inputs.Author}");
    Console.WriteLine($"Branches:   {inputs.BaseBranch} <- {inputs.HeadBranch}");

    // 2. Build Dependency Injection Container
    var services = new ServiceCollection();

    services.AddLogging(builder =>
    {
        builder.AddConsole();
        builder.SetMinimumLevel(LogLevel.Information);
    });

    // Authenticate GitHub client using GITHUB_TOKEN
    services.AddSingleton<IGitHubClient>(_ =>
        new GitHubClient(new ProductHeaderValue("Universal-PR-Bot"))
        {
            Credentials = new Credentials(inputs.GitHubToken)
        });

    // Core services
    services.AddPrBotCore();

    // Register Gemini AI analyzer if API key is provided
    if (!string.IsNullOrWhiteSpace(inputs.GeminiApiKey))
    {
        Console.WriteLine($"🤖 Enabling Gemini AI Code Review ({inputs.GeminiModel})...");
        services.AddGeminiAnalyzer(opts =>
        {
            opts.ApiKey = inputs.GeminiApiKey;
            opts.Model = inputs.GeminiModel;
        });
    }
    else
    {
        Console.WriteLine("⚠️  No GEMINI_API_KEY detected. Please configure GEMINI_API_KEY to enable AI code review.");
    }

    var serviceProvider = services.BuildServiceProvider();

    // 3. Resolve services
    var gitHubService = serviceProvider.GetRequiredService<IGitHubService>();
    var reviewEngine = serviceProvider.GetRequiredService<IReviewEngine>();

    var context = new PullRequestContext
    {
        Owner = inputs.Owner,
        Repo = inputs.Repo,
        PullRequestNumber = inputs.PullRequestNumber,
        Title = inputs.Title,
        Description = inputs.Description,
        Author = inputs.Author,
        BaseBranch = inputs.BaseBranch,
        HeadBranch = inputs.HeadBranch,
        HeadSha = inputs.HeadSha
    };

    // 4. Fetch PR files and diffs
    Console.WriteLine("\n[1/3] Fetching changed files and patches from GitHub...");
    context = await gitHubService.FetchChangedFilesAsync(context);

    if (context.ChangedFiles.Count == 0)
    {
        Console.WriteLine("ℹ️  No reviewable file changes detected in this pull request.");
        return 0;
    }

    // 5. Run all analyzers
    Console.WriteLine("\n[2/3] Running analyzers...");
    var reviewResult = await reviewEngine.RunAsync(context);

    // If any analyzer failed (e.g. AI model unavailable or quota exceeded), abort and fail the job
    var internalErrors = reviewResult.Comments.Where(c => c.FilePath == "Analyzer Internal").ToList();
    if (internalErrors.Count > 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("\n❌ AI Review failed because model was unavailable or quota was exceeded:");
        foreach (var err in internalErrors)
        {
            Console.Error.WriteLine($"  - {err.Body}");
        }
        Console.Error.WriteLine("Aborting job without posting false approval.");
        Console.ResetColor();
        return 1;
    }

    // 6. Post review back to GitHub
    Console.WriteLine("\n[3/3] Posting review to GitHub pull request...");
    await gitHubService.PostReviewAsync(context, reviewResult);

    Console.WriteLine("\n==================================================");
    Console.WriteLine("✅ PR Review completed successfully!");
    Console.WriteLine($"🩺 Code Health Score: {reviewResult.HealthScore}/100 [{reviewResult.HealthGrade}]");
    Console.WriteLine(reviewResult.HealthProgressBar);
    Console.WriteLine($"Findings: {reviewResult.Comments.Count} total ({reviewResult.Comments.Count(c => c.Severity == AnalyzerSeverity.Error)} high, {reviewResult.Comments.Count(c => c.Severity == AnalyzerSeverity.Warning)} medium, {reviewResult.Comments.Count(c => c.Severity == AnalyzerSeverity.Info)} low)");
    Console.WriteLine("==================================================");

    if (inputs.FailOnErrors && reviewResult.HasErrors)
    {
        Console.Error.WriteLine("❌ Failing the GitHub check because critical errors were detected.");
        return 1;
    }

    return 0;
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"\n❌ PR Bot execution failed: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    Console.ResetColor();
    return 1;
}
