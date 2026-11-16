namespace PR_Bot.Core.Tests.Analyzers;

using FluentAssertions;
using Microsoft.Extensions.Options;
using PR_Bot.Core.Analyzers;
using PR_Bot.Core.Configuration;
using PR_Bot.Core.Models;
using Xunit;

public class FileSizeAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_ShouldWarn_WhenFilesCountExceedsLimit()
    {
        // Arrange
        var options = Options.Create(new PrBotOptions { MaxFilesPerPr = 3 });
        var analyzer = new FileSizeAnalyzer(options);

        var files = Enumerable.Range(1, 5).Select(i => new PullRequestFileChange
        {
            FileName = $"file{i}.cs",
            Status = "added",
            Additions = 10,
            Deletions = 0,
            Changes = 10,
            Patch = ""
        }).ToList();

        var context = new PullRequestContext
        {
            Owner = "test",
            Repo = "test",
            PullRequestNumber = 1,
            Title = "Huge PR",
            Author = "author",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha",
            ChangedFiles = files
        };

        // Act
        var comments = await analyzer.AnalyzeAsync(context);

        // Assert
        comments.Should().ContainSingle(c =>
            c.Severity == AnalyzerSeverity.Warning &&
            c.Body.Contains("Large PR Warning"));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldWarn_WhenSingleFileHasLargeAdditions()
    {
        // Arrange
        var options = Options.Create(new PrBotOptions { MaxSingleFileAdditions = 200 });
        var analyzer = new FileSizeAnalyzer(options);

        var context = new PullRequestContext
        {
            Owner = "test",
            Repo = "test",
            PullRequestNumber = 1,
            Title = "Big file PR",
            Author = "author",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "src/Monolith.cs",
                    Status = "added",
                    Additions = 350,
                    Deletions = 0,
                    Changes = 350,
                    Patch = ""
                }
            ]
        };

        // Act
        var comments = await analyzer.AnalyzeAsync(context);

        // Assert
        comments.Should().ContainSingle(c =>
            c.Severity == AnalyzerSeverity.Info &&
            c.Body.Contains("Large File Addition") &&
            c.Body.Contains("+350 lines"));
    }
}
