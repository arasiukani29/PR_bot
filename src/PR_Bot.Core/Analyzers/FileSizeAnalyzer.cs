namespace PR_Bot.Core.Analyzers;

using Microsoft.Extensions.Options;
using PR_Bot.Core.Configuration;
using PR_Bot.Core.Models;

/// <summary>
/// Evaluates PR scope and change volume to encourage smaller, more reviewable pull requests.
/// </summary>
public sealed class FileSizeAnalyzer : IReviewAnalyzer
{
    private readonly PrBotOptions _options;

    public FileSizeAnalyzer(IOptions<PrBotOptions>? options = null)
    {
        _options = options?.Value ?? new PrBotOptions();
    }

    public string Name => "FileSizeAnalyzer";
    public int Order => 30;

    public Task<IReadOnlyList<ReviewComment>> AnalyzeAsync(
        PullRequestContext context,
        CancellationToken cancellationToken = default)
    {
        var comments = new List<ReviewComment>();

        // Check overall files changed count
        if (context.ChangedFiles.Count > _options.MaxFilesPerPr)
        {
            comments.Add(new ReviewComment
            {
                FilePath = "PR Summary",
                Line = 1,
                Body = $"📦 **Large PR Warning:** This pull request modifies {context.ChangedFiles.Count} files (limit recommended: {_options.MaxFilesPerPr}). Consider breaking this into smaller, focused pull requests.",
                Severity = AnalyzerSeverity.Warning,
                AnalyzerName = Name
            });
        }

        // Check individual file additions and total additions
        int totalChanges = 0;

        foreach (var file in context.ChangedFiles)
        {
            totalChanges += file.Changes;

            if (file.Additions > _options.MaxSingleFileAdditions)
            {
                comments.Add(new ReviewComment
                {
                    FilePath = file.FileName,
                    Line = 1,
                    Body = $"📄 **Large File Addition:** +{file.Additions} lines added to `{file.FileName}`. Consider refactoring or splitting into smaller components.",
                    Severity = AnalyzerSeverity.Info,
                    AnalyzerName = Name
                });
            }
        }

        if (totalChanges > _options.MaxTotalChanges)
        {
            comments.Add(new ReviewComment
            {
                FilePath = "PR Summary",
                Line = 1,
                Body = $"📊 **Large Changeset:** Total changes exceed {_options.MaxTotalChanges} lines ({totalChanges} lines changed). Reviews of large PRs often suffer lower defect detection rates.",
                Severity = AnalyzerSeverity.Warning,
                AnalyzerName = Name
            });
        }

        return Task.FromResult<IReadOnlyList<ReviewComment>>(comments);
    }
}
