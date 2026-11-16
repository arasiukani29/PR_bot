namespace PR_Bot.Core.Tests.Analyzers;

using FluentAssertions;
using Microsoft.Extensions.Options;
using PR_Bot.Core.Analyzers;
using PR_Bot.Core.Configuration;
using PR_Bot.Core.Models;
using Xunit;

public class StyleAnalyzerTests
{
    private readonly StyleAnalyzer _analyzer;

    public StyleAnalyzerTests()
    {
        var options = Options.Create(new PrBotOptions { MaxLineLength = 50 });
        _analyzer = new StyleAnalyzer(options);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldDetectLineExceedingMaxLength()
    {
        // Arrange
        var context = new PullRequestContext
        {
            Owner = "test",
            Repo = "test",
            PullRequestNumber = 1,
            Title = "Test PR",
            Author = "author",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "src/Service.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 0,
                    Changes = 1,
                    Patch = "@@ -10,3 +10,4 @@\n context line\n+var reallyLongLineExceedingFiftyCharactersLimitInThisTest = 123456789;\n"
                }
            ]
        };

        // Act
        var comments = await _analyzer.AnalyzeAsync(context);

        // Assert
        comments.Should().ContainSingle(c =>
            c.FilePath == "src/Service.cs" &&
            c.Line == 11 &&
            c.Severity == AnalyzerSeverity.Warning &&
            c.Body.Contains("Line exceeds 50 characters"));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldDetectTrailingWhitespace()
    {
        // Arrange
        var context = new PullRequestContext
        {
            Owner = "test",
            Repo = "test",
            PullRequestNumber = 1,
            Title = "Test PR",
            Author = "author",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "src/Helper.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 0,
                    Changes = 1,
                    Patch = "@@ -1,1 +1,2 @@\n+var x = 10;   \n"
                }
            ]
        };

        // Act
        var comments = await _analyzer.AnalyzeAsync(context);

        // Assert
        comments.Should().ContainSingle(c =>
            c.FilePath == "src/Helper.cs" &&
            c.Severity == AnalyzerSeverity.Info &&
            c.Body.Contains("Trailing whitespace"));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldReturnEmpty_ForCleanDiff()
    {
        // Arrange
        var context = new PullRequestContext
        {
            Owner = "test",
            Repo = "test",
            PullRequestNumber = 1,
            Title = "Test PR",
            Author = "author",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "src/Clean.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 0,
                    Changes = 1,
                    Patch = "@@ -1,1 +1,2 @@\n+var clean = 42;\n"
                }
            ]
        };

        // Act
        var comments = await _analyzer.AnalyzeAsync(context);

        // Assert
        comments.Should().BeEmpty();
    }
}
