namespace PR_Bot.Core.Tests.Services;

using FluentAssertions;
using Moq;
using PR_Bot.Core.Analyzers;
using PR_Bot.Core.Models;
using PR_Bot.Core.Services;
using Xunit;

public class ReviewEngineTests
{
    [Fact]
    public async Task RunAsync_ShouldAggregateFindingsFromAllAnalyzers()
    {
        // Arrange
        var analyzer1 = new Mock<IReviewAnalyzer>();
        analyzer1.SetupGet(a => a.Name).Returns("Mock1");
        analyzer1.SetupGet(a => a.Order).Returns(10);
        analyzer1.Setup(a => a.AnalyzeAsync(It.IsAny<PullRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ReviewComment { FilePath = "file1.cs", Line = 5, Body = "Issue 1", AnalyzerName = "Mock1" }
            ]);

        var analyzer2 = new Mock<IReviewAnalyzer>();
        analyzer2.SetupGet(a => a.Name).Returns("Mock2");
        analyzer2.SetupGet(a => a.Order).Returns(20);
        analyzer2.Setup(a => a.AnalyzeAsync(It.IsAny<PullRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ReviewComment { FilePath = "file2.cs", Line = 15, Body = "Issue 2", AnalyzerName = "Mock2" }
            ]);

        var engine = new ReviewEngine([analyzer1.Object, analyzer2.Object]);

        var context = new PullRequestContext
        {
            Owner = "test",
            Repo = "test",
            PullRequestNumber = 1,
            Title = "PR",
            Author = "user",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha"
        };

        // Act
        var result = await engine.RunAsync(context);

        // Assert
        result.Comments.Should().HaveCount(2);
        result.Comments.Should().Contain(c => c.AnalyzerName == "Mock1");
        result.Comments.Should().Contain(c => c.AnalyzerName == "Mock2");
    }

    [Fact]
    public async Task RunAsync_ShouldSurviveFailingAnalyzer()
    {
        // Arrange
        var faultyAnalyzer = new Mock<IReviewAnalyzer>();
        faultyAnalyzer.SetupGet(a => a.Name).Returns("Faulty");
        faultyAnalyzer.Setup(a => a.AnalyzeAsync(It.IsAny<PullRequestContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated unexpected crash"));

        var healthyAnalyzer = new Mock<IReviewAnalyzer>();
        healthyAnalyzer.SetupGet(a => a.Name).Returns("Healthy");
        healthyAnalyzer.Setup(a => a.AnalyzeAsync(It.IsAny<PullRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ReviewComment { FilePath = "ok.cs", Line = 1, Body = "All good", AnalyzerName = "Healthy" }
            ]);

        var engine = new ReviewEngine([faultyAnalyzer.Object, healthyAnalyzer.Object]);

        var context = new PullRequestContext
        {
            Owner = "test",
            Repo = "test",
            PullRequestNumber = 1,
            Title = "PR",
            Author = "user",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha"
        };

        // Act
        var result = await engine.RunAsync(context);

        // Assert
        result.Comments.Should().Contain(c => c.AnalyzerName == "Healthy");
        result.Comments.Should().Contain(c => c.AnalyzerName == "Faulty" && c.Body.Contains("Simulated unexpected crash"));
    }

    [Theory]
    [InlineData(0, 0, 0, 100, "A (Excellent)")]
    [InlineData(1, 0, 0, 75, "B (Good)")]
    [InlineData(1, 1, 0, 65, "C (Needs Attention)")]
    [InlineData(4, 0, 0, 0, "F (Critical Action Required)")]
    public void ReviewResult_HealthScore_CalculatesAccurately(
        int highCount, int mediumCount, int lowCount, int expectedScore, string expectedGrade)
    {
        var comments = new List<ReviewComment>();
        for (int i = 0; i < highCount; i++) comments.Add(new ReviewComment { FilePath = "f.cs", Line = 1, Body = "err", Severity = AnalyzerSeverity.Error, AnalyzerName = "test" });
        for (int i = 0; i < mediumCount; i++) comments.Add(new ReviewComment { FilePath = "f.cs", Line = 1, Body = "warn", Severity = AnalyzerSeverity.Warning, AnalyzerName = "test" });
        for (int i = 0; i < lowCount; i++) comments.Add(new ReviewComment { FilePath = "f.cs", Line = 1, Body = "info", Severity = AnalyzerSeverity.Info, AnalyzerName = "test" });

        var result = new ReviewResult { Comments = comments };

        result.HealthScore.Should().Be(expectedScore);
        result.HealthGrade.Should().Be(expectedGrade);
        result.HealthProgressBar.Should().Contain($"{expectedScore}%");
    }
}
