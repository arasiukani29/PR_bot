# 🛠️ Writing Custom Analyzers for PR Bot

PR Bot features a fully pluggable analyzer architecture. Any class that implements `IReviewAnalyzer` can be registered into the dependency injection container and will be picked up by the `ReviewEngine`.

---

## The `IReviewAnalyzer` Interface

```csharp
namespace PR_Bot.Core.Analyzers;

using PR_Bot.Core.Models;

public interface IReviewAnalyzer
{
    /// <summary>
    /// Display name shown in the review summary table.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Execution order priority (lower numbers execute first). Default is 100.
    /// </summary>
    int Order => 100;

    /// <summary>
    /// Analyzes the PR context and returns zero or more findings.
    /// </summary>
    Task<IReadOnlyList<ReviewComment>> AnalyzeAsync(
        PullRequestContext context,
        CancellationToken cancellationToken = default);
}
```

---

## Example 1: Naming Convention Analyzer

Here is an example analyzer that ensures all new C# interface files start with the letter `I`:

```csharp
using PR_Bot.Core.Analyzers;
using PR_Bot.Core.Models;

public sealed class InterfaceNamingAnalyzer : IReviewAnalyzer
{
    public string Name => "InterfaceNaming";
    public int Order => 15;

    public Task<IReadOnlyList<ReviewComment>> AnalyzeAsync(
        PullRequestContext context,
        CancellationToken cancellationToken = default)
    {
        var comments = new List<ReviewComment>();

        foreach (var file in context.ChangedFiles)
        {
            if (file.Patch == null || !file.FileName.EndsWith(".cs")) continue;

            if (file.Patch.Contains("interface ") && !Path.GetFileNameWithoutExtension(file.FileName).StartsWith("I"))
            {
                comments.Add(new ReviewComment
                {
                    FilePath = file.FileName,
                    Line = 1,
                    Body = "⚠️ **Interface Naming:** C# interface file names should start with the prefix 'I'.",
                    Severity = AnalyzerSeverity.Warning,
                    AnalyzerName = Name
                });
            }
        }

        return Task.FromResult<IReadOnlyList<ReviewComment>>(comments);
    }
}
```

---

## Example 2: Banned Dependency / NuGet Analyzer

Checks if changes introduce deprecated or prohibited packages in `.csproj`:

```csharp
using PR_Bot.Core.Analyzers;
using PR_Bot.Core.Models;

public sealed class BannedPackageAnalyzer : IReviewAnalyzer
{
    public string Name => "BannedPackageAnalyzer";
    public int Order => 8;

    private static readonly string[] BannedPackages = ["Newtonsoft.Json", "log4net"];

    public Task<IReadOnlyList<ReviewComment>> AnalyzeAsync(
        PullRequestContext context,
        CancellationToken cancellationToken = default)
    {
        var comments = new List<ReviewComment>();

        foreach (var file in context.ChangedFiles)
        {
            if (!file.FileName.EndsWith(".csproj") || file.Patch == null) continue;

            foreach (var banned in BannedPackages)
            {
                if (file.Patch.Contains(banned))
                {
                    comments.Add(new ReviewComment
                    {
                        FilePath = file.FileName,
                        Line = 1,
                        Body = $"🚨 **Banned Package:** `{banned}` is deprecated in this organization. Please use modern alternatives (e.g. `System.Text.Json`).",
                        Severity = AnalyzerSeverity.Error,
                        AnalyzerName = Name
                    });
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ReviewComment>>(comments);
    }
}
```

---

## Registering Your Custom Analyzer

Simply add your analyzer to the DI container:

```csharp
services.AddPrBotCore();
services.AddSingleton<IReviewAnalyzer, InterfaceNamingAnalyzer>();
services.AddSingleton<IReviewAnalyzer, BannedPackageAnalyzer>();
```
