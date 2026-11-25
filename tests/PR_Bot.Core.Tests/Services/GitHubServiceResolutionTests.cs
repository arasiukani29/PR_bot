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

        _issueCommentsMock
            .Setup(i => i.GetAllForIssue(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<IssueComment>());

        _reviewCommentsMock
            .Setup(r => r.CreateReply(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<PullRequestReviewCommentReplyCreate>()))
            .ReturnsAsync(CreateComment(9999, "path", "reply"));

        var connectionMock = new Mock<IConnection>();
        _gitHubClientMock.SetupGet(c => c.Connection).Returns(connectionMock.Object);
    }

    private static PullRequestReviewComment CreateComment(
        long id,
        string path,
        string body,
        string? diffHunk = null,
        long? inReplyToId = null)
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
            inReplyToId: inReplyToId,
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
        capturedEdit!.Body.Should().Contain("### ✅ [RESOLVED]");
        capturedEdit.Body.Should().Contain("SQL injection vulnerability.");
        capturedEdit.Body.Should().Contain("<!-- pr-bot-resolved -->");

        _reviewCommentsMock.Verify(
            r => r.Edit("test-owner", "test-repo", 1001, It.IsAny<PullRequestReviewCommentEdit>()),
            Times.Once);

        _reviewCommentsMock.Verify(
            r => r.CreateReply("test-owner", "test-repo", 42, It.Is<PullRequestReviewCommentReplyCreate>(c =>
                c.Body.Contains("✅ Resolved in `1234567`") &&
                c.Body.Contains("<summary>Changes that fixed it</summary>") &&
                c.Body.Contains("+ fixed_code;"))),
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
                    IsResolved = false,
                    ReconciliationStatus = "still_active" // AI explicitly confirmed NOT resolved!
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

        // Act: hasNewDefects = true means a new review batch is being submitted, so active comments roll forward
        await service.ReconcileResolvedCommentsAsync(context, result, hasNewDefects: true);

        // Assert:
        // Comment 1001 MUST be edited in-place
        _reviewCommentsMock.Verify(
            r => r.Edit("test-owner", "test-repo", 1001, It.IsAny<PullRequestReviewCommentEdit>()),
            Times.Once);

        // Comment 1001 MUST NEVER be deleted!
        _reviewCommentsMock.Verify(
            r => r.Delete("test-owner", "test-repo", 1001),
            Times.Never);

        // Comment 1002 MUST NOT be edited as resolved
        _reviewCommentsMock.Verify(
            r => r.Edit("test-owner", "test-repo", 1002, It.IsAny<PullRequestReviewCommentEdit>()),
            Times.Never);

        // Comment 1002 (active) MUST NOT be deleted so thread history is preserved!
        _reviewCommentsMock.Verify(
            r => r.Delete("test-owner", "test-repo", 1002),
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

    [Fact]
    public async Task PostReviewAsync_WhenNoSummaryCommentExists_CreatesSingleOverallComment()
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

        _issueCommentsMock
            .Setup(i => i.GetAllForIssue("test-owner", "test-repo", 42))
            .ReturnsAsync(new List<IssueComment>());

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(new List<PullRequestReviewComment>());

        var pullRequestReviewClientMock = new Mock<IPullRequestReviewsClient>();
        _pullRequestClientMock.SetupGet(p => p.Review).Returns(pullRequestReviewClientMock.Object);
        pullRequestReviewClientMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(new List<PullRequestReview>());

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        var result = new ReviewResult();

        // Act
        await service.PostReviewAsync(context, result);

        // Assert: Should CREATE the single overall comment
        _issueCommentsMock.Verify(
            i => i.Create("test-owner", "test-repo", 42, It.Is<string>(b => b.Contains(GitHubService.BotCommentMarker))),
            Times.Once);

        _issueCommentsMock.Verify(
            i => i.Update(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task PostReviewAsync_WhenPreviouslyResolvedDefectReintroduced_ReportsAsNewRegression()
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
            HeadSha = "new_commit_sha",
            ChangedFiles = [],
            PriorDefects =
            [
                new PriorReviewDefect
                {
                    CommentId = 888,
                    FilePath = "Program.cs",
                    Line = 10,
                    Title = "Singleton lifetime bug",
                    OriginalBody = "Singleton service holds state.",
                    Severity = AnalyzerSeverity.Error,
                    IsResolved = true,
                    WasPreviouslyResolved = true
                }
            ]
        };

        // Existing comments on PR: A previously resolved comment on Program.cs line 10
        var existingComments = new List<PullRequestReviewComment>
        {
            CreateComment(
                888,
                "Program.cs",
                "### ✅ [RESOLVED] Issue Fixed in `old_sha`\n<!-- pr-bot-finding:file=Program.cs:line=10 -->\n<!-- pr-bot-resolved -->")
        };

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(existingComments);

        var pullRequestReviewClientMock = new Mock<IPullRequestReviewsClient>();
        _pullRequestClientMock.SetupGet(p => p.Review).Returns(pullRequestReviewClientMock.Object);
        pullRequestReviewClientMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(new List<PullRequestReview>());

        ReviewSubmissionPayload? capturedPayload = null;
        var connectionMock = new Mock<IConnection>();
        connectionMock
            .Setup(c => c.Post<PullRequestReview>(
                It.IsAny<Uri>(),
                It.IsAny<ReviewSubmissionPayload>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Uri, object, string, string, IDictionary<string, string>, CancellationToken>(
                (_, payload, _, _, _, _) => capturedPayload = payload as ReviewSubmissionPayload)
            .ReturnsAsync(new Mock<IApiResponse<PullRequestReview>>().Object);

        _gitHubClientMock.SetupGet(c => c.Connection).Returns(connectionMock.Object);

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Developer re-introduced the exact same bug on line 10!
        var result = new ReviewResult
        {
            Comments =
            [
                new ReviewComment
                {
                    FilePath = "Program.cs",
                    Line = 10,
                    Title = "Singleton lifetime bug (reintroduced)",
                    Body = "Singleton service holds state.",
                    Severity = AnalyzerSeverity.Error,
                    AnalyzerName = "Gemini AI"
                }
            ]
        };

        // Act
        await service.PostReviewAsync(context, result);

        // Assert:
        // Must NOT skip as duplicate! Must be submitted as a new regression comment!
        capturedPayload.Should().NotBeNull();
        capturedPayload!.Comments.Should().NotBeNull();
        capturedPayload.Comments.Should().ContainSingle(c => c.Path == "Program.cs" && c.Line == 10);
    }

    [Fact]
    public async Task PostReviewAsync_RollsForwardActivePriorDefectsToLatestReview()
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
            HeadSha = "commit_2_sha",
            ChangedFiles = [],
            PriorDefects =
            [
                new PriorReviewDefect
                {
                    CommentId = 777,
                    FilePath = "Controllers/TasksController.cs",
                    Line = 34,
                    Title = "Missing null check",
                    OriginalBody = "DTO can be null",
                    Severity = AnalyzerSeverity.Warning,
                    IsResolved = false // Active!
                }
            ]
        };

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(new List<PullRequestReviewComment>());

        var pullRequestReviewClientMock = new Mock<IPullRequestReviewsClient>();
        _pullRequestClientMock.SetupGet(p => p.Review).Returns(pullRequestReviewClientMock.Object);
        pullRequestReviewClientMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(new List<PullRequestReview>());

        ReviewSubmissionPayload? capturedPayload = null;
        var connectionMock = new Mock<IConnection>();
        connectionMock
            .Setup(c => c.Post<PullRequestReview>(
                It.IsAny<Uri>(),
                It.IsAny<ReviewSubmissionPayload>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Uri, object, string, string, IDictionary<string, string>, CancellationToken>(
                (_, payload, _, _, _, _) => capturedPayload = payload as ReviewSubmissionPayload)
            .ReturnsAsync(new Mock<IApiResponse<PullRequestReview>>().Object);

        _gitHubClientMock.SetupGet(c => c.Connection).Returns(connectionMock.Object);

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Result from Gemini in this run has a new bug on OrderService.cs, but didn't re-report TasksController
        var result = new ReviewResult
        {
            Comments =
            [
                new ReviewComment
                {
                    FilePath = "Services/OrderService.cs",
                    Line = 55,
                    Title = "Unhandled exception",
                    Body = "Try catch missing.",
                    Severity = AnalyzerSeverity.Error,
                    AnalyzerName = "Gemini AI"
                }
            ],
            ActivePriorDefects =
            [
                context.PriorDefects[0]
            ]
        };

        // Act
        await service.PostReviewAsync(context, result);

        // Assert:
        // Review submission MUST contain BOTH comments:
        // 1) The active prior defect rolled forward (TasksController.cs:34)
        // 2) The new defect (OrderService.cs:55)
        capturedPayload.Should().NotBeNull();
        capturedPayload!.Comments.Should().NotBeNull();
        capturedPayload.Comments.Should().HaveCount(2);
        capturedPayload.Comments.Should().Contain(c => c.Path == "Controllers/TasksController.cs" && c.Line == 34);
        capturedPayload.Comments.Should().Contain(c => c.Path == "Services/OrderService.cs" && c.Line == 55);
    }

    [Fact]
    public async Task ReconcileResolvedCommentsAsync_WhenCommentIsActive_PreservesCommentInExistingThread()
    {
        // Real-world GitHub scenario: GitHub updates comment.CommitId to the PR HeadSha.
        // The active comment MUST NOT be deleted so thread history and discussions are preserved.
        var context = new PullRequestContext
        {
            Owner = "test-owner",
            Repo = "test-repo",
            PullRequestNumber = 42,
            Title = "PR 42",
            Author = "dev",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "head_sha_123",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "Services/PaymentService.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 0,
                    Changes = 1,
                    Patch = "@@ -20,2 +20,3 @@"
                }
            ],
            PriorDefects =
            [
                new PriorReviewDefect
                {
                    CommentId = 999,
                    FilePath = "Services/PaymentService.cs",
                    Line = 22,
                    Title = "Unchecked currency",
                    OriginalBody = "Unchecked currency",
                    IsResolved = false // Still active!
                }
            ]
        };

        // GitHub API returns comment with commitId matching HeadSha
        var comment = new PullRequestReviewComment(
            url: "https://api.github.com",
            id: 999,
            nodeId: "MDEy",
            diffHunk: "@@ -20,2 +20,3 @@",
            path: "Services/PaymentService.cs",
            position: 1,
            originalPosition: 1,
            commitId: "head_sha_123", // MATCHES HeadSha!
            originalCommitId: "old_sha",
            user: new User(),
            body: "Unchecked currency\n<!-- pr-bot-finding:file=Services/PaymentService.cs:line=22 -->",
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            htmlUrl: "https://github.com",
            pullRequestUrl: "https://github.com/pr/42",
            reactions: null!,
            inReplyToId: null,
            pullRequestReviewId: 1,
            authorAssociation: AuthorAssociation.None);

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync([comment]);

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Act
        var deletedIds = await service.ReconcileResolvedCommentsAsync(context, new ReviewResult(), hasNewDefects: true);

        // Assert: Comment MUST NOT be deleted so thread history is preserved
        _reviewCommentsMock.Verify(
            r => r.Delete("test-owner", "test-repo", 999),
            Times.Never);

        deletedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task PostReviewAsync_WhenDefectIsResolved_RemovesAnyDuplicateFindingFromCommentsSoItIsNotCountedAsActiveBug()
    {
        // Arrange: PR has a defect that was resolved in this run
        var context = new PullRequestContext
        {
            Owner = "test-owner",
            Repo = "test-repo",
            PullRequestNumber = 42,
            Title = "PR 42",
            Author = "dev",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "head_sha_456",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "Models/CreateTaskDto.cs",
                    Status = "modified",
                    Additions = 2,
                    Deletions = 1,
                    Changes = 3,
                    Patch = "@@ -9,2 +9,4 @@\n- old\n+ [Required]\n+ [StringLength(120)]\n+ public string Title"
                }
            ],
            PriorDefects =
            [
                new PriorReviewDefect
                {
                    CommentId = 888,
                    FilePath = "Models/CreateTaskDto.cs",
                    Line = 9,
                    Title = "Missing length validation",
                    OriginalBody = "Add StringLength attribute",
                    IsResolved = true, // AI verified as resolved!
                    ResolutionExplanation = "Added StringLength(120)"
                }
            ]
        };

        var comment = CreateComment(888, "Models/CreateTaskDto.cs", "Add StringLength attribute\n<!-- pr-bot-finding:file=Models/CreateTaskDto.cs:line=9 -->");

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync([comment]);

        var pullRequestReviewClientMock = new Mock<IPullRequestReviewsClient>();
        _pullRequestClientMock.SetupGet(p => p.Review).Returns(pullRequestReviewClientMock.Object);
        pullRequestReviewClientMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(new List<PullRequestReview>());

        ReviewSubmissionPayload? capturedPayload = null;
        var connectionMock = new Mock<IConnection>();
        connectionMock
            .Setup(c => c.Post<PullRequestReview>(
                It.IsAny<Uri>(),
                It.IsAny<ReviewSubmissionPayload>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Uri, object, string, string, IDictionary<string, string>, CancellationToken>(
                (_, payload, _, _, _, _) => capturedPayload = payload as ReviewSubmissionPayload)
            .ReturnsAsync(new Mock<IApiResponse<PullRequestReview>>().Object);

        _gitHubClientMock.SetupGet(c => c.Connection).Returns(connectionMock.Object);

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Result from analyzer contains a residual finding on the same file & line (e.g. Low suggestion)
        var result = new ReviewResult
        {
            Comments =
            [
                new ReviewComment
                {
                    FilePath = "Models/CreateTaskDto.cs",
                    Line = 9,
                    Title = "Optional doc comment",
                    Body = "Add XML doc.",
                    Severity = AnalyzerSeverity.Info,
                    AnalyzerName = "Gemini AI"
                }
            ]
        };

        // Act
        await service.PostReviewAsync(context, result);

        // Assert:
        // 1. Result.Comments must have purged the resolved line finding!
        result.Comments.Should().BeEmpty();
        result.TotalDefects.Should().Be(0);
        result.HealthScore.Should().Be(100);

        // 2. The single overall comment must show Approved to Merge with verified fix!
        _issueCommentsMock.Verify(
            i => i.Create("test-owner", "test-repo", 42, It.Is<string>(b =>
                b.Contains("PR Review: Approved to Merge") &&
                b.Contains("verified fix") &&
                !b.Contains("Code Health Score"))),
            Times.Once);

        // 3. Review submission body must be concise without duplicating the full dashboard
        capturedPayload.Should().NotBeNull();
        capturedPayload!.Body.Should().Contain("Approved based on automated review findings");
    }

    [Fact]
    public async Task PostReviewAsync_WhenResolvedBugReadded_DetectsRegressionAndReportsDefect()
    {
        // Scenario: A defect on Program.cs line 10 was previously resolved in an earlier commit.
        // Developer re-introduces the exact same bug in the latest commit.
        // Expected:
        // - The defect MUST be detected as a regression!
        // - It MUST NOT be purged by resolved defect filtering!
        // - It MUST appear in result.Comments and be counted as an active defect on the scorecard!
        // - It MUST NOT appear in result.ResolvedDefects!
        var context = new PullRequestContext
        {
            Owner = "test-owner",
            Repo = "test-repo",
            PullRequestNumber = 42,
            Title = "PR 42",
            Author = "dev",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "head_sha_regression",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "Program.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 1,
                    Changes = 2,
                    Patch = "@@ -10,1 +10,1 @@\n- scoped;\n+ singleton;"
                }
            ],
            PriorDefects =
            [
                new PriorReviewDefect
                {
                    CommentId = 777,
                    FilePath = "Program.cs",
                    Line = 10,
                    Title = "Singleton lifetime bug",
                    OriginalBody = "Use Scoped lifetime\n<!-- pr-bot-finding:file=Program.cs:line=10 -->",
                    Severity = AnalyzerSeverity.Error,
                    IsResolved = true,
                    WasPreviouslyResolved = true // Was resolved in previous commit!
                }
            ]
        };

        var comment = CreateComment(
            777,
            "Program.cs",
            "### ✅ [RESOLVED] Issue Fixed in `commit_1`\n<!-- pr-bot-finding:file=Program.cs:line=10 -->\n<!-- pr-bot-resolved -->");

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync([comment]);

        var pullRequestReviewClientMock = new Mock<IPullRequestReviewsClient>();
        _pullRequestClientMock.SetupGet(p => p.Review).Returns(pullRequestReviewClientMock.Object);
        pullRequestReviewClientMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(new List<PullRequestReview>());

        ReviewSubmissionPayload? capturedPayload = null;
        var connectionMock = new Mock<IConnection>();
        connectionMock
            .Setup(c => c.Post<PullRequestReview>(
                It.IsAny<Uri>(),
                It.IsAny<ReviewSubmissionPayload>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Uri, object, string, string, IDictionary<string, string>, CancellationToken>(
                (_, payload, _, _, _, _) => capturedPayload = payload as ReviewSubmissionPayload)
            .ReturnsAsync(new Mock<IApiResponse<PullRequestReview>>().Object);

        _gitHubClientMock.SetupGet(c => c.Connection).Returns(connectionMock.Object);

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Analyzer re-detects the bug on line 10!
        var result = new ReviewResult
        {
            Comments =
            [
                new ReviewComment
                {
                    FilePath = "Program.cs",
                    Line = 10,
                    Title = "Singleton lifetime bug (reintroduced)",
                    Body = "Service must use Scoped lifetime, not Singleton.",
                    Severity = AnalyzerSeverity.Error,
                    AnalyzerName = "Gemini AI"
                }
            ]
        };

        // Act
        await service.PostReviewAsync(context, result);

        // Assert:
        // 1. Defect MUST be retained in Comments as an active defect!
        result.Comments.Should().ContainSingle(c => c.FilePath == "Program.cs" && c.Line == 10);
        result.TotalDefects.Should().Be(1);
        result.HighCount.Should().Be(1);

        // 2. Defect MUST NOT be listed as resolved!
        result.ResolvedDefects.Should().BeEmpty();

        // 3. The single overall comment must flag the defect with Merge Blocked!
        _issueCommentsMock.Verify(
            i => i.Create("test-owner", "test-repo", 42, It.Is<string>(b =>
                b.Contains("PR Review: Merge Blocked") &&
                b.Contains("Action Required (Critical & High Priority)") &&
                !b.Contains("Code Health Score"))),
            Times.Once);

        // 4. Review submission contains inline comment and concise body
        capturedPayload.Should().NotBeNull();
        capturedPayload!.Comments.Should().NotBeNull();
        capturedPayload.Comments.Should().ContainSingle(c => c.Path == "Program.cs" && c.Line == 10);
        capturedPayload.Body.Should().Contain("Changes requested based on automated review findings");
    }

    [Fact]
    public void BuildReviewMarkdown_WithAllPriorities_RendersOptionABadgesAndPrioritySections()
    {
        // Arrange
        var context = new PullRequestContext
        {
            Owner = "test-owner",
            Repo = "test-repo",
            PullRequestNumber = 42,
            Title = "Feature PR",
            Author = "developer",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "abc1234567",
            ChangedFiles = []
        };

        var result = new ReviewResult
        {
            Comments =
            [
                new ReviewComment
                {
                    FilePath = "Services/AuthService.cs",
                    Line = 10,
                    Title = "SQL injection",
                    Category = "Security",
                    Severity = AnalyzerSeverity.Critical,
                    AnalyzerName = "Gemini AI",
                    Body = "Vulnerability"
                },
                new ReviewComment
                {
                    FilePath = "Services/OrderService.cs",
                    Line = 25,
                    Title = "Potential Null Reference",
                    Category = "Reliability",
                    Severity = AnalyzerSeverity.Warning,
                    AnalyzerName = "Gemini AI",
                    Body = "Null check missing"
                },
                new ReviewComment
                {
                    FilePath = "Helpers/StringHelper.cs",
                    Line = 40,
                    Title = "Use pattern matching",
                    Category = "Maintainability",
                    Severity = AnalyzerSeverity.Info,
                    AnalyzerName = "Gemini AI",
                    Body = "Idiomatic code"
                }
            ],
            ResolvedDefects =
            [
                new PriorReviewDefect
                {
                    CommentId = 111,
                    FilePath = "Models/UserDto.cs",
                    Line = 5,
                    Title = "Missing Trim",
                    OriginalBody = "Add trim",
                    IsResolved = true,
                    ResolutionExplanation = "Added Trim() on Title"
                }
            ],
            SummaryMarkdown = "Reviewed auth and order processing logic."
        };

        // Act
        var markdown = GitHubService.BuildReviewMarkdown(context, result);

        // Assert
        markdown.Should().Contain("### ❌ PR Review: Merge Blocked");
        markdown.Should().Contain("1 blocking issue require attention before this pull request can be merged.");
        markdown.Should().Contain("❌ Security · ⚠️ Reliability · ⚠️ Code Quality");
        markdown.Should().Contain("🔴 **Action Required (Critical & High Priority)**");
        markdown.Should().Contain("SQL injection · [Services/AuthService.cs:10](https://github.com/test-owner/test-repo/blob/abc1234567/Services/AuthService.cs#L10) ↗");
        markdown.Should().Contain("🟡 **Recommended Improvements (Medium Priority)**");
        markdown.Should().Contain("Potential Null Reference · [Services/OrderService.cs:25](https://github.com/test-owner/test-repo/blob/abc1234567/Services/OrderService.cs#L25) ↗");
        markdown.Should().Contain("🟢 **Optional Suggestions (Low Priority)**");
        markdown.Should().Contain("Use pattern matching · [Helpers/StringHelper.cs:40](https://github.com/test-owner/test-repo/blob/abc1234567/Helpers/StringHelper.cs#L40) ↗");
        markdown.Should().Contain("<details>");
        markdown.Should().Contain("<summary><b>✅ 1 verified fix</b></summary>");
        markdown.Should().Contain("Missing Trim");
        markdown.Should().Contain("*(Fixed in `abc1234`: Added Trim() on Title)*");
        markdown.Should().NotContain("Code Health Score");
        markdown.Should().NotContain("Scorecard");
    }

    [Fact]
    public async Task ReconcileResolvedCommentsAsync_WhenResolved_PostsReplyToThreadAndResolvesGraphQL()
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
            HeadSha = "fedcba9876",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "Services/UserService.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 1,
                    Changes = 2,
                    Patch = "@@ -10,3 +10,4 @@\n- old\n+ fixed"
                }
            ]
        };

        var comment = CreateComment(2001, "Services/UserService.cs", "Bug here\n<!-- pr-bot-finding:file=Services/UserService.cs:line=11 -->");

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync([comment]);

        _patchExtractorMock
            .Setup(p => p.ExtractResolutionDiff(It.IsAny<string>(), 11))
            .Returns("- old\n+ fixed");

        PullRequestReviewCommentReplyCreate? capturedReply = null;
        _reviewCommentsMock
            .Setup(r => r.CreateReply("test-owner", "test-repo", 42, It.IsAny<PullRequestReviewCommentReplyCreate>()))
            .Callback<string, string, int, PullRequestReviewCommentReplyCreate>((_, _, _, r) => capturedReply = r)
            .ReturnsAsync(CreateComment(2002, "Services/UserService.cs", "reply"));

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Act (result has 0 defects on line 11)
        await service.ReconcileResolvedCommentsAsync(context, new ReviewResult());

        // Assert: Thread reply was created with resolution diff
        capturedReply.Should().NotBeNull();
        capturedReply!.Body.Should().Contain("✅ Resolved in `fedcba9`.");
        capturedReply.Body.Should().Contain("<summary>Changes that fixed it</summary>");
        capturedReply.Body.Should().Contain("- old\n+ fixed");
        capturedReply.Body.Should().Contain(GitHubService.ResolvedMarker);

        // Assert: Comment was NOT deleted
        _reviewCommentsMock.Verify(
            r => r.Delete(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>()),
            Times.Never);
    }

    [Fact]
    public async Task ReconcileResolvedCommentsAsync_ResolvesReviewThreadViaGraphQL()
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
            HeadSha = "fedcba9876543210",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "Services/UserService.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 1,
                    Changes = 2,
                    Patch = "@@ -10,3 +10,4 @@\n- old\n+ fixed"
                }
            ]
        };

        var comment = CreateComment(3001, "Services/UserService.cs", "Bug here\n<!-- pr-bot-finding:file=Services/UserService.cs:line=11 -->");

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync([comment]);

        _patchExtractorMock
            .Setup(p => p.ExtractResolutionDiff(It.IsAny<string>(), 11))
            .Returns("- old\n+ fixed");

        var connectionMock = new Mock<IConnection>();
        var queryResponseJson = """
        {
          "data": {
            "repository": {
              "pullRequest": {
                "reviewThreads": {
                  "nodes": [
                    {
                      "id": "PRRT_test123",
                      "isResolved": false,
                      "comments": {
                        "nodes": [
                          {
                            "databaseId": 3001
                          }
                        ]
                      }
                    }
                  ]
                }
              }
            }
          }
        }
        """;

        var queryApiResponseMock = new Mock<IApiResponse<object>>();
        var queryHttpResponseMock = new Mock<IResponse>();
        queryHttpResponseMock.SetupGet(h => h.Body).Returns(queryResponseJson);
        queryApiResponseMock.SetupGet(a => a.HttpResponse).Returns(queryHttpResponseMock.Object);

        var mutationApiResponseMock = new Mock<IApiResponse<object>>();

        object? capturedMutationPayload = null;

        connectionMock
            .Setup(c => c.Post<object>(
                It.Is<Uri>(u => u.OriginalString == "graphql"),
                It.Is<object>(o => o.ToString()!.Contains("reviewThreads")),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryApiResponseMock.Object);

        connectionMock
            .Setup(c => c.Post<object>(
                It.Is<Uri>(u => u.OriginalString == "graphql"),
                It.Is<object>(o => o.ToString()!.Contains("resolveReviewThread")),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Uri, object, string, string, IDictionary<string, string>, CancellationToken>(
                (_, payload, _, _, _, _) => capturedMutationPayload = payload)
            .ReturnsAsync(mutationApiResponseMock.Object);

        _gitHubClientMock.SetupGet(c => c.Connection).Returns(connectionMock.Object);

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Act
        await service.ReconcileResolvedCommentsAsync(context, new ReviewResult());

        // Assert: GraphQL mutation was executed with PRRT_test123
        capturedMutationPayload.Should().NotBeNull();
        capturedMutationPayload.ToString().Should().Contain("PRRT_test123");
    }

    [Fact]
    public async Task ReconcileResolvedCommentsAsync_WhenFileIsRemoved_MarksCommentResolvedWithFileRemovedHeader()
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
                    FileName = "Services/LegacyService.cs",
                    Status = "removed",
                    Additions = 0,
                    Deletions = 40,
                    Changes = 40
                }
            ]
        };

        var existingComments = new List<PullRequestReviewComment>
        {
            CreateComment(
                4001,
                "Services/LegacyService.cs",
                "Legacy bug.\n<!-- pr-bot-finding:file=Services/LegacyService.cs:line=15 -->")
        };

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(existingComments);

        PullRequestReviewCommentEdit? capturedEdit = null;
        _reviewCommentsMock
            .Setup(r => r.Edit("test-owner", "test-repo", 4001, It.IsAny<PullRequestReviewCommentEdit>()))
            .Callback<string, string, long, PullRequestReviewCommentEdit>((_, _, _, edit) => capturedEdit = edit)
            .ReturnsAsync(CreateComment(4001, "Services/LegacyService.cs", "updated"));

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Act
        await service.ReconcileResolvedCommentsAsync(context, new ReviewResult());

        // Assert
        capturedEdit.Should().NotBeNull();
        capturedEdit!.Body.Should().Contain("### ✅ [RESOLVED - File Removed]");
        capturedEdit.Body.Should().Contain(GitHubService.ResolvedMarker);
    }

    [Fact]
    public async Task ReconcileResolvedCommentsAsync_WhenUnfixedDefectShiftsBeyondTenLines_DoesNotFalselyResolve()
    {
        // Arrange: Defect originally at line 20 shifted to line 35 (same title in same file)
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
                    Additions = 15,
                    Deletions = 0,
                    Changes = 15,
                    Patch = "@@ -1,3 +1,18 @@\n+ // 15 lines of comments\n@@ -19,2 +34,2 @@\n+ var x = 1;"
                }
            ],
            PriorDefects =
            [
                new PriorReviewDefect
                {
                    CommentId = 5001,
                    FilePath = "Services/AuthService.cs",
                    Line = 20,
                    Title = "SQL Injection",
                    OriginalBody = "SQL Injection",
                    IsResolved = false
                }
            ]
        };

        var existingComments = new List<PullRequestReviewComment>
        {
            CreateComment(
                5001,
                "Services/AuthService.cs",
                "SQL Injection\n<!-- pr-bot-finding:file=Services/AuthService.cs:line=20 -->")
        };

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync(existingComments);

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        var currentResult = new ReviewResult
        {
            Comments =
            [
                new ReviewComment
                {
                    FilePath = "Services/AuthService.cs",
                    Line = 35,
                    Title = "SQL Injection",
                    Body = "SQL Injection still here",
                    AnalyzerName = "GeminiAI"
                }
            ]
        };

        // Act
        await service.ReconcileResolvedCommentsAsync(context, currentResult);

        // Assert: Must NOT mark comment #5001 as resolved because same-title defect is still active at line 35!
        _reviewCommentsMock.Verify(
            r => r.Edit(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<PullRequestReviewCommentEdit>()),
            Times.Never);
    }

    [Fact]
    public async Task ReconcileResolvedCommentsAsync_PaginatesGraphQLThreadsBeyondFirst100()
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
            HeadSha = "fedcba9876543210",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "Services/UserService.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 1,
                    Changes = 2,
                    Patch = "@@ -10,3 +10,4 @@\n- old\n+ fixed"
                }
            ]
        };

        var comment = CreateComment(6001, "Services/UserService.cs", "Bug\n<!-- pr-bot-finding:file=Services/UserService.cs:line=11 -->");

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync([comment]);

        var page1Json = """
        {
          "data": {
            "repository": {
              "pullRequest": {
                "reviewThreads": {
                  "pageInfo": { "hasNextPage": true, "endCursor": "cursor_page_2" },
                  "nodes": [
                    {
                      "id": "PRRT_other",
                      "isResolved": false,
                      "comments": { "nodes": [ { "databaseId": 1111 } ] }
                    }
                  ]
                }
              }
            }
          }
        }
        """;

        var page2Json = """
        {
          "data": {
            "repository": {
              "pullRequest": {
                "reviewThreads": {
                  "pageInfo": { "hasNextPage": false, "endCursor": null },
                  "nodes": [
                    {
                      "id": "PRRT_page2_match",
                      "isResolved": false,
                      "comments": { "nodes": [ { "databaseId": 6001 } ] }
                    }
                  ]
                }
              }
            }
          }
        }
        """;

        var page1ResponseMock = new Mock<IApiResponse<object>>();
        var page1HttpMock = new Mock<IResponse>();
        page1HttpMock.SetupGet(h => h.Body).Returns(page1Json);
        page1ResponseMock.SetupGet(a => a.HttpResponse).Returns(page1HttpMock.Object);

        var page2ResponseMock = new Mock<IApiResponse<object>>();
        var page2HttpMock = new Mock<IResponse>();
        page2HttpMock.SetupGet(h => h.Body).Returns(page2Json);
        page2ResponseMock.SetupGet(a => a.HttpResponse).Returns(page2HttpMock.Object);

        var mutationResponseMock = new Mock<IApiResponse<object>>();
        object? capturedMutationPayload = null;

        var connectionMock = new Mock<IConnection>();
        connectionMock
            .SetupSequence(c => c.Post<object>(
                It.Is<Uri>(u => u.OriginalString == "graphql"),
                It.Is<object>(o => o.ToString()!.Contains("reviewThreads")),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(page1ResponseMock.Object)
            .ReturnsAsync(page2ResponseMock.Object);

        connectionMock
            .Setup(c => c.Post<object>(
                It.Is<Uri>(u => u.OriginalString == "graphql"),
                It.Is<object>(o => o.ToString()!.Contains("resolveReviewThread")),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Uri, object, string, string, IDictionary<string, string>, CancellationToken>(
                (_, payload, _, _, _, _) => capturedMutationPayload = payload)
            .ReturnsAsync(mutationResponseMock.Object);

        _gitHubClientMock.SetupGet(c => c.Connection).Returns(connectionMock.Object);

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Act
        await service.ReconcileResolvedCommentsAsync(context, new ReviewResult());

        // Assert
        capturedMutationPayload.Should().NotBeNull();
        capturedMutationPayload!.ToString().Should().Contain("PRRT_page2_match");
    }

    [Fact]
    public async Task PopulatePriorDefectsAsync_SkipsResolutionReplyCommentsAndExtractsOriginalTitle()
    {
        // Arrange: Parent resolved comment + bot's own resolution reply in the same thread
        var context = new PullRequestContext
        {
            Owner = "test-owner",
            Repo = "test-repo",
            PullRequestNumber = 42,
            Title = "PR 42",
            Author = "dev",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "5522df1"
        };

        var parentComment = CreateComment(
            1001,
            "Models/TagDto.cs",
            "### ✅ [RESOLVED]\n\n🔴 **[HIGH]** Missing validation on Name\n\n<!-- pr-bot-finding:file=Models/TagDto.cs:line=13 -->\n<!-- pr-bot-resolved -->");

        var replyComment = CreateComment(
            1002,
            "Models/TagDto.cs",
            "✅ Resolved in `7daf16e`.\n\n<!-- pr-bot-resolved -->",
            inReplyToId: 1001);

        _reviewCommentsMock
            .Setup(r => r.GetAll("test-owner", "test-repo", 42))
            .ReturnsAsync([parentComment, replyComment]);

        var service = new GitHubService(
            _gitHubClientMock.Object,
            patchExtractor: _patchExtractorMock.Object);

        // Act
        await service.PopulatePriorDefectsAsync(context);

        // Assert: Only the parent comment is loaded (no duplicate from reply), with the real finding title
        context.PriorDefects.Should().ContainSingle();
        context.PriorDefects[0].CommentId.Should().Be(1001);
        context.PriorDefects[0].Line.Should().Be(13);
        context.PriorDefects[0].IsResolved.Should().BeTrue();
        context.PriorDefects[0].Title.Should().Be("🔴 **[HIGH]** Missing validation on Name");
    }
}
