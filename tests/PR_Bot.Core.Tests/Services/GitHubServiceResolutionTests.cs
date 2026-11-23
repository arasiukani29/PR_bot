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

    private static User CreateUser(string login)
    {
        var user = new User();
        typeof(Account).GetProperty("Login")?.GetSetMethod(true)?.Invoke(user, [login]);
        return user;
    }

    private static PullRequestReview CreateReview(long id, PullRequestReviewState state)
    {
        return new PullRequestReview(
            id,
            "nodeId",
            "commitId",
            CreateUser("github-actions[bot]"),
            "body",
            "htmlUrl",
            "pullRequestUrl",
            state,
            AuthorAssociation.None,
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task PostReviewAsync_SubmitsAllInOneReview_WithDashboardAsReviewBodyAndNoSeparateIssueComment()
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

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(new List<PullRequestReviewComment>());

        var pullRequestReviewClientMock = new Mock<IPullRequestReviewsClient>();
        _pullRequestClientMock.SetupGet(p => p.Review).Returns(pullRequestReviewClientMock.Object);
        pullRequestReviewClientMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(new List<PullRequestReview>());

        var connectionMock = new Mock<IConnection>();
        var putResponseMock = new Mock<IApiResponse<PullRequestReview>>();
        connectionMock
            .Setup(c => c.Put<PullRequestReview>(
                It.IsAny<Uri>(),
                It.IsAny<object>()))
            .ReturnsAsync(putResponseMock.Object);
        _gitHubClientMock.SetupGet(c => c.Connection).Returns(connectionMock.Object);

        var apiResponseMock = new Mock<IApiResponse<PullRequestReview>>();
        apiResponseMock.SetupGet(r => r.Body).Returns(CreateReview(1, PullRequestReviewState.Approved));

        object? capturedReviewPayload = null;
        connectionMock
            .Setup(c => c.Post<PullRequestReview>(
                It.Is<Uri>(u => u.ToString().Contains("/reviews")),
                It.IsAny<object>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Uri, object, string, string, IDictionary<string, string>, CancellationToken>((_, payload, _, _, _, _) =>
                capturedReviewPayload = payload)
            .ReturnsAsync(apiResponseMock.Object);

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        var result = new ReviewResult();

        // Act
        await service.PostReviewAsync(context, result);

        // Assert: All-in-One Review submitted with dashboard table inside review body
        capturedReviewPayload.Should().NotBeNull();
        var bodyProp = capturedReviewPayload!.GetType().GetProperty("Body")?.GetValue(capturedReviewPayload) as string;
        bodyProp.Should().NotBeNull();
        bodyProp.Should().Contain(GitHubService.BotCommentMarker);
        bodyProp.Should().Contain("Code Quality & Health Scorecard");

        // Verify that NO separate Issue Comment was created!
        _issueCommentsMock.Verify(
            i => i.Create(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);
        _issueCommentsMock.Verify(
            i => i.Update(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task PostReviewAsync_WhenPreviousReviewExistsAndStateUnchanged_UpdatesReviewBodyInPlaceViaPut()
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

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(new List<PullRequestReviewComment>());

        var existingReview = CreateReview(888, PullRequestReviewState.Approved);

        var pullRequestReviewClientMock = new Mock<IPullRequestReviewsClient>();
        _pullRequestClientMock.SetupGet(p => p.Review).Returns(pullRequestReviewClientMock.Object);
        pullRequestReviewClientMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(new List<PullRequestReview> { existingReview });

        var connectionMock = new Mock<IConnection>();
        var putResponseMock = new Mock<IApiResponse<PullRequestReview>>();
        connectionMock
            .Setup(c => c.Put<PullRequestReview>(
                It.IsAny<Uri>(),
                It.IsAny<object>()))
            .ReturnsAsync(putResponseMock.Object);
        _gitHubClientMock.SetupGet(c => c.Connection).Returns(connectionMock.Object);

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Result has 0 defects (Approve)
        var result = new ReviewResult();

        // Act
        await service.PostReviewAsync(context, result);

        // Assert: Calls PUT on reviews/888 to update in-place without creating a new review
        connectionMock.Verify(
            c => c.Put<PullRequestReview>(
                It.Is<Uri>(u => u.ToString().Contains("/reviews/888")),
                It.IsAny<object>()),
            Times.Once);

        // Did not post a new review
        connectionMock.Verify(
            c => c.Post<PullRequestReview>(
                It.IsAny<Uri>(),
                It.IsAny<object>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
