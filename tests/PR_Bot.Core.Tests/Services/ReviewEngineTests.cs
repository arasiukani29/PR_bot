namespace PR_Bot.Core.Tests.Services;

using FluentAssertions;
using Moq;
using Octokit;
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

    [Fact]
    public void BuildReviewMarkdown_WithCriticalIssues_RendersCautionAlertAndDashboard()
    {
        var context = new PullRequestContext
        {
            Owner = "owner",
            Repo = "repo",
            PullRequestNumber = 42,
            Title = "feat: add auth",
            Author = "dev",
            BaseBranch = "main",
            HeadBranch = "feature/auth",
            HeadSha = "abc",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "src/AuthService.cs",
                    Status = "modified",
                    Additions = 20,
                    Deletions = 5,
                    Changes = 25
                }
            ]
        };

        var result = new ReviewResult
        {
            Comments =
            [
                new ReviewComment
                {
                    FilePath = "src/AuthService.cs",
                    Line = 15,
                    Title = "SQL Injection in Login",
                    Category = "Security",
                    WhyItMatters = "Exposes database to arbitrary queries",
                    Body = "Vulnerable query concatenation",
                    Severity = AnalyzerSeverity.Critical,
                    AnalyzerName = "GeminiAI"
                }
            ],
            SummaryMarkdown = "Executive summary of changes."
        };

        var markdown = GitHubService.BuildReviewMarkdown(context, result);

        // Assertions for UI elements
        markdown.Should().Contain(GitHubService.BotCommentMarker);
        markdown.Should().Contain("### ❌ PR Review: Merge Blocked");
        markdown.Should().Contain("1 blocking issue require attention before this pull request can be merged.");
        markdown.Should().Contain("❌ Security");
        markdown.Should().Contain("🔴 **Action Required (Critical & High Priority)**");
        markdown.Should().Contain("Executive summary of changes.");
        markdown.Should().NotContain("Health Scorecard");
    }

    [Fact]
    public void BuildReviewMarkdown_WithCleanPR_RendersTipAlertAndPassedGate()
    {
        var context = new PullRequestContext
        {
            Owner = "owner",
            Repo = "repo",
            PullRequestNumber = 10,
            Title = "feat: clean code",
            Author = "dev",
            BaseBranch = "main",
            HeadBranch = "feature/clean",
            HeadSha = "abc",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "src/Helper.cs",
                    Status = "added",
                    Additions = 10,
                    Deletions = 0,
                    Changes = 10
                }
            ]
        };

        var result = new ReviewResult { Comments = [] };

        var markdown = GitHubService.BuildReviewMarkdown(context, result);

        markdown.Should().Contain("### ✅ PR Review: Approved to Merge");
        markdown.Should().Contain("All automated quality and security checks have passed with zero blockers. Ready to merge! 🎉");
        markdown.Should().Contain("✅ Security · ✅ Reliability · ✅ Code Quality");
        markdown.Should().NotContain("Health Score");
    }

    [Fact]
    public void HealthProgressBar_RendersGradeMatchingEmojiBlocks()
    {
        // Grade A (100) -> 🟩
        new ReviewResult { Comments = [] }.HealthProgressBar
            .Should().Be("🟩🟩🟩🟩🟩🟩🟩🟩🟩🟩 &nbsp; **100%**");

        // Grade B (75) -> 🟨
        var gradeB = new ReviewResult
        {
            Comments = [new ReviewComment { FilePath = "f.cs", Line = 1, Body = "err", Severity = AnalyzerSeverity.Error, AnalyzerName = "t" }]
        };
        gradeB.HealthScore.Should().Be(75);
        gradeB.HealthProgressBar.Should().Be("🟨🟨🟨🟨🟨🟨🟨🟨⬛⬛ &nbsp; **75%**");

        // Grade C (65) -> 🟧 (6 filled, 4 empty = 10 blocks)
        var gradeC = new ReviewResult
        {
            Comments =
            [
                new ReviewComment { FilePath = "f.cs", Line = 1, Body = "err", Severity = AnalyzerSeverity.Error, AnalyzerName = "t" },
                new ReviewComment { FilePath = "f.cs", Line = 2, Body = "warn", Severity = AnalyzerSeverity.Warning, AnalyzerName = "t" }
            ]
        };
        gradeC.HealthScore.Should().Be(65);
        gradeC.HealthProgressBar.Should().Be("🟧🟧🟧🟧🟧🟧⬛⬛⬛⬛ &nbsp; **65%**");

        // Grade F (0) -> 🟥
        var gradeF = new ReviewResult
        {
            Comments =
            [
                new ReviewComment { FilePath = "f.cs", Line = 1, Body = "c1", Severity = AnalyzerSeverity.Critical, AnalyzerName = "t" },
                new ReviewComment { FilePath = "f.cs", Line = 2, Body = "c2", Severity = AnalyzerSeverity.Critical, AnalyzerName = "t" },
                new ReviewComment { FilePath = "f.cs", Line = 3, Body = "c3", Severity = AnalyzerSeverity.Critical, AnalyzerName = "t" },
                new ReviewComment { FilePath = "f.cs", Line = 4, Body = "c4", Severity = AnalyzerSeverity.Critical, AnalyzerName = "t" }
            ]
        };
        gradeF.HealthScore.Should().Be(0);
        gradeF.HealthProgressBar.Should().Be("⬛⬛⬛⬛⬛⬛⬛⬛⬛⬛ &nbsp; **0%**");
    }

    [Fact]
    public void DetermineReviewEvent_DynamicMode_CriticalOrHigh_ReturnsRequestChanges()
    {
        var critResult = new ReviewResult
        {
            Comments = [new ReviewComment { FilePath = "f.cs", Line = 1, Body = "crit", Severity = AnalyzerSeverity.Critical, AnalyzerName = "t" }]
        };
        GitHubService.DetermineReviewEvent(critResult, ReviewEventMode.Dynamic)
            .Should().Be(PullRequestReviewEvent.RequestChanges);

        var highResult = new ReviewResult
        {
            Comments = [new ReviewComment { FilePath = "f.cs", Line = 1, Body = "high", Severity = AnalyzerSeverity.Error, AnalyzerName = "t" }]
        };
        GitHubService.DetermineReviewEvent(highResult, ReviewEventMode.Dynamic)
            .Should().Be(PullRequestReviewEvent.RequestChanges);
    }

    [Fact]
    public void DetermineReviewEvent_DynamicMode_MediumOrLow_ReturnsComment()
    {
        var medResult = new ReviewResult
        {
            Comments =
            [
                new ReviewComment { FilePath = "f.cs", Line = 1, Body = "med1", Severity = AnalyzerSeverity.Warning, AnalyzerName = "t" },
                new ReviewComment { FilePath = "f.cs", Line = 2, Body = "med2", Severity = AnalyzerSeverity.Warning, AnalyzerName = "t" }
            ]
        };
        GitHubService.DetermineReviewEvent(medResult, ReviewEventMode.Dynamic)
            .Should().Be(PullRequestReviewEvent.Comment);

        var lowResult = new ReviewResult
        {
            Comments = [new ReviewComment { FilePath = "f.cs", Line = 1, Body = "low", Severity = AnalyzerSeverity.Info, AnalyzerName = "t" }]
        };
        GitHubService.DetermineReviewEvent(lowResult, ReviewEventMode.Dynamic)
            .Should().Be(PullRequestReviewEvent.Comment);
    }

    [Fact]
    public void DetermineReviewEvent_DynamicMode_CleanPR_ReturnsApprove()
    {
        var cleanResult = new ReviewResult { Comments = [] };
        GitHubService.DetermineReviewEvent(cleanResult, ReviewEventMode.Dynamic)
            .Should().Be(PullRequestReviewEvent.Approve);
    }

    [Fact]
    public void DetermineReviewEvent_StrictMode_MediumIssues_ReturnsRequestChanges()
    {
        var medResult = new ReviewResult
        {
            Comments = [new ReviewComment { FilePath = "f.cs", Line = 1, Body = "med", Severity = AnalyzerSeverity.Warning, AnalyzerName = "t" }]
        };
        GitHubService.DetermineReviewEvent(medResult, ReviewEventMode.Strict)
            .Should().Be(PullRequestReviewEvent.RequestChanges);

        var cleanResult = new ReviewResult { Comments = [] };
        GitHubService.DetermineReviewEvent(cleanResult, ReviewEventMode.Strict)
            .Should().Be(PullRequestReviewEvent.Approve);
    }

    [Fact]
    public void DetermineReviewEvent_CommentOnlyMode_AlwaysReturnsComment()
    {
        var critResult = new ReviewResult
        {
            Comments = [new ReviewComment { FilePath = "f.cs", Line = 1, Body = "crit", Severity = AnalyzerSeverity.Critical, AnalyzerName = "t" }]
        };
        GitHubService.DetermineReviewEvent(critResult, ReviewEventMode.CommentOnly)
            .Should().Be(PullRequestReviewEvent.Comment);

        var cleanResult = new ReviewResult { Comments = [] };
        GitHubService.DetermineReviewEvent(cleanResult, ReviewEventMode.CommentOnly)
            .Should().Be(PullRequestReviewEvent.Comment);
    }

    [Fact]
    public void HasDefectsAtOrAbove_ShouldFilterBySeverityThreshold()
    {
        var result = new ReviewResult
        {
            Comments =
            [
                new ReviewComment { FilePath = "f.cs", Line = 1, Body = "warn", Severity = AnalyzerSeverity.Warning, AnalyzerName = "t" },
                new ReviewComment { FilePath = "f.cs", Line = 2, Body = "info", Severity = AnalyzerSeverity.Info, AnalyzerName = "t" }
            ]
        };

        // Warning is Medium (value 1)
        result.HasDefectsAtOrAbove(AnalyzerSeverity.Warning).Should().BeTrue();
        result.HasDefectsAtOrAbove(AnalyzerSeverity.Error).Should().BeFalse();
        result.HasDefectsAtOrAbove(AnalyzerSeverity.Critical).Should().BeFalse();

        // Add a Critical comment
        result.Comments.Add(new ReviewComment { FilePath = "f.cs", Line = 3, Body = "crit", Severity = AnalyzerSeverity.Critical, AnalyzerName = "t" });
        result.HasDefectsAtOrAbove(AnalyzerSeverity.Critical).Should().BeTrue();
        result.HasDefectsAtOrAbove(AnalyzerSeverity.Error).Should().BeTrue();
        result.HasDefectsAtOrAbove(AnalyzerSeverity.Warning).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WhenPriorDefectIsNewlyResolved_KeepsUnrelatedNewFindingInSameFile()
    {
        // Arrange: Prior defect at line 10 is newly resolved; analyzer reports a brand-new defect at line 100 in the same file.
        var analyzer = new Mock<IReviewAnalyzer>();
        analyzer.SetupGet(a => a.Name).Returns("Mock");
        analyzer.Setup(a => a.AnalyzeAsync(It.IsAny<PullRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ReviewComment { FilePath = "AuthService.cs", Line = 100, Body = "New bug at line 100", AnalyzerName = "Mock" }
            ]);

        var engine = new ReviewEngine([analyzer.Object]);

        var context = new PullRequestContext
        {
            Owner = "test",
            Repo = "test",
            PullRequestNumber = 1,
            Title = "PR",
            Author = "user",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha",
            PriorDefects =
            [
                new PriorReviewDefect
                {
                    CommentId = 10,
                    FilePath = "AuthService.cs",
                    Line = 10,
                    Title = "Old bug",
                    OriginalBody = "Old bug",
                    IsResolved = true,
                    WasPreviouslyResolved = false
                }
            ]
        };

        // Act
        var result = await engine.RunAsync(context);

        // Assert: The unrelated new finding at line 100 MUST be preserved!
        result.Comments.Should().ContainSingle(c => c.FilePath == "AuthService.cs" && c.Line == 100);
    }
}
