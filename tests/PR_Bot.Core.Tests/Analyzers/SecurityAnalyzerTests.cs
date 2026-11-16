namespace PR_Bot.Core.Tests.Analyzers;

using FluentAssertions;
using PR_Bot.Core.Analyzers;
using PR_Bot.Core.Models;
using Xunit;

public class SecurityAnalyzerTests
{
    private readonly SecurityAnalyzer _analyzer = new();

    [Fact]
    public async Task AnalyzeAsync_ShouldDetectPrivateKey()
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
                    FileName = "cert.pem",
                    Status = "added",
                    Additions = 1,
                    Deletions = 0,
                    Changes = 1,
                    Patch = "@@ -0,0 +1,1 @@\n+-----BEGIN RSA PRIVATE KEY-----\nMIIEogIBAAKCAQEA..."
                }
            ]
        };

        // Act
        var comments = await _analyzer.AnalyzeAsync(context);

        // Assert
        comments.Should().ContainSingle(c =>
            c.Severity == AnalyzerSeverity.Error &&
            c.Body.Contains("Private Key Detected"));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldDetectHardcodedApiKey()
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
                    FileName = "appsettings.json",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 0,
                    Changes = 1,
                    Patch = "@@ -5,1 +5,2 @@\n+\"api_key\": \"AIzaSyB31TestKey1234567890\","
                }
            ]
        };

        // Act
        var comments = await _analyzer.AnalyzeAsync(context);

        // Assert
        comments.Should().ContainSingle(c =>
            c.Severity == AnalyzerSeverity.Error &&
            c.Body.Contains("Possible Hardcoded Secret"));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldDetectSslBypass()
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
                    FileName = "HttpClientSetup.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 0,
                    Changes = 1,
                    Patch = "@@ -1,1 +1,2 @@\n+handler.ServerCertificateCustomValidationCallback = (m, c, ch, e) => true;"
                }
            ]
        };

        // Act
        var comments = await _analyzer.AnalyzeAsync(context);

        // Assert
        comments.Should().ContainSingle(c =>
            c.Severity == AnalyzerSeverity.Error &&
            c.Body.Contains("Certificate Validation Bypass"));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldReturnEmpty_ForSafeCode()
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
                    FileName = "Program.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 0,
                    Changes = 1,
                    Patch = "@@ -1,1 +1,2 @@\n+var apiKey = Environment.GetEnvironmentVariable(\"API_KEY\");"
                }
            ]
        };

        // Act
        var comments = await _analyzer.AnalyzeAsync(context);

        // Assert
        comments.Should().BeEmpty();
    }
}
