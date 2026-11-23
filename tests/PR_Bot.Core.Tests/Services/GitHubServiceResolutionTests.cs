namespace PR_Bot.Core.Tests.Services;

using FluentAssertions;
using Moq;
using Octokit;
using PR_Bot.Core.Models;
using PR_Bot.Core.Services;
using Xunit;

public class GitHubServiceResolutionTests
{
    private readonly Mock<IGitHubClient> _gitHubClientMock = new();
    private readonly Mock<IPullRequestsClient> _pullRequestClientMock = new();
    private readonly Mock<IPullRequestReviewCommentsClient> _reviewCommentsMock = new();
    private readonly Mock<IIssuesClient> _issuesClientMock = new();
    private readonly Mock<IIssueCommentsClient> _issueCommentsMock = new();
    private readonly Mock<IPatchResolutionExtractor> _patchExtractorMock = new();

    public GitHubServiceResolutionTests()
    {
        _gitHubClientMock.SetupGet(c => c.PullRequest).Returns(_pullRequestClientMock.Object);
        _pullRequestClientMock.SetupGet(p => p.ReviewComment).Returns(_reviewCommentsMock.Object);

        _gitHubClientMock.SetupGet(c => c.Issue).Returns(_issuesClientMock.Object);
        _issuesClientMock.SetupGet(i => i.Comment).Returns(_issueCommentsMock.Object);
    }

    private static PullRequestReviewComment CreateComment(
        long id,
        string path,
        string body,
        string? diffHunk = null)
    {
        return new PullRequestReviewComment(
            url: "https://api.github.com",
            id: id,
            nodeId: "MDEy",
            diffHunk: diffHunk ?? "@@ -1,1 +1,1 @@",
            path: path,
            position: 1,
            originalPosition: 1,
            commitId: "sha1",
            originalCommitId: "sha1",
            user: new User(),
            body: body,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            htmlUrl: "https://github.com",
            pullRequestUrl: "https://github.com/pr/1",
            reactions: null!,
            inReplyToId: null,
            pullRequestReviewId: 1,
            authorAssociation: AuthorAssociation.None);
    }

    private static IssueComment CreateIssueComment(long id, string body)
    {
        return new IssueComment(
            id,
            "nodeId",
            "https://api.github.com",
            "https://github.com",
            body,
            DateTimeOffset.UtcNow,
            null,
            new User(),
            null!,
            AuthorAssociation.None);
    }

    [Fact]
    public async Task ReconcileResolvedCommentsAsync_WhenDefectIsResolved_UpdatesCommentInPlaceWithDiff()
    {
        // Arrange
        var context = new PullRequestContext
        {
            Owner = "test-owner",
            Repo = "test-repo",
            PullRequestNumber = 42,
            Title = "PR 42",
            Author = "dev",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "1234567890abcdef",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "Services/AuthService.cs",
                    Status = "modified",
                    Additions = 2,
                    Deletions = 1,
                    Changes = 3,
                    Patch = "@@ -50,3 +50,4 @@\n- old_code;\n+ fixed_code;\n+ extra_check;"
                }
            ]
        };

        var existingComments = new List<PullRequestReviewComment>
        {
            CreateComment(
                1001,
                "Services/AuthService.cs",
                "SQL injection vulnerability.\n\n<sub>Reported by Gemini</sub>\n<!-- pr-bot-finding:file=Services/AuthService.cs:line=51 -->",
                "@@ -50,3 +50,4 @@")
        };

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(existingComments);

        _patchExtractorMock
            .Setup(e => e.ExtractResolutionDiff(It.IsAny<string>(), 51))
            .Returns("+ fixed_code;\n+ extra_check;");

        PullRequestReviewCommentEdit? capturedEdit = null;
        _reviewCommentsMock
            .Setup(r => r.Edit("test-owner", "test-repo", 1001, It.IsAny<PullRequestReviewCommentEdit>()))
            .Callback<string, string, long, PullRequestReviewCommentEdit>((_, _, _, edit) => capturedEdit = edit)
            .ReturnsAsync(CreateComment(1001, "Services/AuthService.cs", "updated"));

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Current review result has 0 defects on AuthService.cs (it was resolved!)
        var currentResult = new ReviewResult();

        // Act
        await service.ReconcileResolvedCommentsAsync(context, currentResult);

        // Assert
        capturedEdit.Should().NotBeNull();
        capturedEdit!.Body.Should().Contain("### ✅ [RESOLVED] Issue Fixed in `1234567`");
        capturedEdit.Body.Should().Contain("+ fixed_code;");
        capturedEdit.Body.Should().Contain("+ extra_check;");
        capturedEdit.Body.Should().Contain("<details>");
        capturedEdit.Body.Should().Contain("SQL injection vulnerability.");
        capturedEdit.Body.Should().Contain("<!-- pr-bot-resolved -->");

        _reviewCommentsMock.Verify(
            r => r.Edit("test-owner", "test-repo", 1001, It.IsAny<PullRequestReviewCommentEdit>()),
            Times.Once);
    }

    [Fact]
    public async Task ReconcileResolvedCommentsAsync_WhenDefectStillPresent_DoesNotEditComment()
    {
        // Arrange
        var context = new PullRequestContext
        {
            Owner = "test-owner",
            Repo = "test-repo",
            PullRequestNumber = 42,
            Title = "PR 42",
            Author = "dev",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "1234567890abcdef",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "Services/AuthService.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 0,
                    Changes = 1,
                    Patch = "@@ -50,3 +50,4 @@"
                }
            ]
        };

        var existingComments = new List<PullRequestReviewComment>
        {
            CreateComment(
                1001,
                "Services/AuthService.cs",
                "SQL injection.\n<!-- pr-bot-finding:file=Services/AuthService.cs:line=51 -->")
        };

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(existingComments);

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Current result STILL flags the issue at line 51!
        var currentResult = new ReviewResult
        {
            Comments =
            [
                new ReviewComment
                {
                    FilePath = "Services/AuthService.cs",
                    Line = 51,
                    Body = "Still vulnerable",
                    AnalyzerName = "Security"
                }
            ]
        };

        // Act
        await service.ReconcileResolvedCommentsAsync(context, currentResult);

        // Assert - comment should NOT be edited because defect is still present
        _reviewCommentsMock.Verify(
            r => r.Edit(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<PullRequestReviewCommentEdit>()),
            Times.Never);
    }

    [Fact]
    public async Task ReconcileResolvedCommentsAsync_WhenCommentAlreadyResolved_SkipsEdit()
    {
        // Arrange
        var context = new PullRequestContext
        {
            Owner = "test-owner",
            Repo = "test-repo",
            PullRequestNumber = 42,
            Title = "PR 42",
            Author = "dev",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "1234567890abcdef",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "Services/AuthService.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 0,
                    Changes = 1
                }
            ]
        };

        var existingComments = new List<PullRequestReviewComment>
        {
            CreateComment(
                1001,
                "Services/AuthService.cs",
                "### ✅ [RESOLVED] Issue Fixed in `abcdef1`\n<!-- pr-bot-resolved -->")
        };

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(existingComments);

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Act
        await service.ReconcileResolvedCommentsAsync(context, new ReviewResult());

        // Assert
        _reviewCommentsMock.Verify(
            r => r.Edit(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<PullRequestReviewCommentEdit>()),
            Times.Never);
    }

    [Fact]
    public async Task ReconcileResolvedCommentsAsync_WhenOnlyOneOfMultipleDefectsResolved_OnlyUpdatesResolvedComment()
    {
        // Arrange: 2 previous comments exist. Developer fixed #1001, but NOT #1002.
        var context = new PullRequestContext
        {
            Owner = "test-owner",
            Repo = "test-repo",
            PullRequestNumber = 42,
            Title = "PR 42",
            Author = "dev",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "1234567890abcdef",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "Program.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 1,
                    Changes = 2,
                    Patch = "@@ -9,3 +9,3 @@\n- AddSingleton\n+ AddScoped"
                },
                new PullRequestFileChange
                {
                    FileName = "Models/CreateTaskDto.cs",
                    Status = "added",
                    Additions = 22,
                    Deletions = 0,
                    Changes = 22,
                    Patch = "@@ -0,0 +1,22 @@\n+ class CreateTaskDto"
                }
            ],
            PriorDefects =
            [
                new PriorReviewDefect
                {
                    CommentId = 1001,
                    FilePath = "Program.cs",
                    Line = 10,
                    Title = "Singleton lifetime",
                    OriginalBody = "Use Scoped",
                    IsResolved = true, // AI verified as resolved
                    ResolutionExplanation = "Changed to AddScoped"
                },
                new PriorReviewDefect
                {
                    CommentId = 1002,
                    FilePath = "Models/CreateTaskDto.cs",
                    Line = 9,
                    Title = "Missing whitespace trim",
                    OriginalBody = "Add trim",
                    IsResolved = false // NOT resolved!
                }
            ]
        };

        var existingComments = new List<PullRequestReviewComment>
        {
            CreateComment(1001, "Program.cs", "Use Scoped\n<!-- pr-bot-finding:file=Program.cs:line=10 -->"),
            CreateComment(1002, "Models/CreateTaskDto.cs", "Add trim\n<!-- pr-bot-finding:file=Models/CreateTaskDto.cs:line=9 -->")
        };

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(existingComments);

        _patchExtractorMock
            .Setup(e => e.ExtractResolutionDiff(It.IsAny<string>(), 10))
            .Returns("- AddSingleton\n+ AddScoped");

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Result from latest run (has 0 comments because Gemini didn't repeat 1002)
        var result = new ReviewResult();

        // Act
        await service.ReconcileResolvedCommentsAsync(context, result);

        // Assert:
        // Comment 1001 MUST be edited in-place
        _reviewCommentsMock.Verify(
            r => r.Edit("test-owner", "test-repo", 1001, It.IsAny<PullRequestReviewCommentEdit>()),
            Times.Once);

        // Comment 1002 MUST NOT be edited in-place (must stay open because developer did not fix it!)
        _reviewCommentsMock.Verify(
            r => r.Edit("test-owner", "test-repo", 1002, It.IsAny<PullRequestReviewCommentEdit>()),
            Times.Never);
    }

    [Fact]
    public async Task PostReviewAsync_WhenExistingSummaryCommentExists_UpdatesInPlaceInsteadOfCreatingNew()
    {
        // Arrange
        var context = new PullRequestContext
        {
            Owner = "test-owner",
            Repo = "test-repo",
            PullRequestNumber = 42,
            Title = "PR 42",
            Author = "dev",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "1234567890abcdef",
            ChangedFiles = []
        };

        var existingIssueComments = new List<IssueComment>
        {
            CreateIssueComment(555, $"Previous review dashboard\n{GitHubService.BotCommentMarker}")
        };

        _issueCommentsMock
            .Setup(i => i.GetAllForIssue("test-owner", "test-repo", 42))
            .ReturnsAsync(existingIssueComments);

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(new List<PullRequestReviewComment>());

        var pullRequestReviewClientMock = new Mock<IPullRequestReviewsClient>();
        _pullRequestClientMock.SetupGet(p => p.Review).Returns(pullRequestReviewClientMock.Object);
        pullRequestReviewClientMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(new List<PullRequestReview>());

        var connectionMock = new Mock<IConnection>();
        _gitHubClientMock.SetupGet(c => c.Connection).Returns(connectionMock.Object);

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        var result = new ReviewResult();

        // Act
        await service.PostReviewAsync(context, result);

        // Assert
        // Should UPDATE existing comment 555, NOT CREATE a new one!
        _issueCommentsMock.Verify(
            i => i.Update("test-owner", "test-repo", 555, It.Is<string>(b => b.Contains(GitHubService.BotCommentMarker))),
            Times.Once);

        _issueCommentsMock.Verify(
            i => i.Create(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);
    }
}
