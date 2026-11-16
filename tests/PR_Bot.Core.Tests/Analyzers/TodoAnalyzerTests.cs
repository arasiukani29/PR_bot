namespace PR_Bot.Core.Tests.Analyzers;

using FluentAssertions;
using PR_Bot.Core.Analyzers;
using PR_Bot.Core.Models;
using Xunit;

public class TodoAnalyzerTests
{
    private readonly TodoAnalyzer _analyzer = new();

    [Theory]
    [InlineData("+// TODO: implement caching layer", "TODO")]
    [InlineData("+// FIXME: fix race condition in queue", "FIXME")]
    [InlineData("+// HACK: temporary workaround for bug #42", "HACK")]
    [InlineData("+/* XXX: check thread safety */", "XXX")]
    public async Task AnalyzeAsync_ShouldDetectTechnicalDebtMarkers(string addedLine, string expectedMarker)
    {
        // Arrange
        var context = new PullRequestContext
        {
            Owner = "test",
            Repo = "test",
            PullRequestNumber = 1,
            Title = "Test",
            Author = "author",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "src/OrderService.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 0,
                    Changes = 1,
                    Patch = $"@@ -1,1 +1,2 @@\n{addedLine}\n"
                }
            ]
        };

        // Act
        var comments = await _analyzer.AnalyzeAsync(context);

        // Assert
        comments.Should().ContainSingle(c =>
            c.Severity == AnalyzerSeverity.Info &&
            c.Body.Contains(expectedMarker));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldIgnoreRegularComments()
    {
        // Arrange
        var context = new PullRequestContext
        {
            Owner = "test",
            Repo = "test",
            PullRequestNumber = 1,
            Title = "Test",
            Author = "author",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "src/OrderService.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 0,
                    Changes = 1,
                    Patch = "@@ -1,1 +1,2 @@\n+// Calculate the total invoice amount\n"
                }
            ]
        };

        // Act
        var comments = await _analyzer.AnalyzeAsync(context);

        // Assert
        comments.Should().BeEmpty();
    }
}
