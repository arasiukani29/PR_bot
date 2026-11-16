namespace PR_Bot.Core.Tests.Analyzers;

using FluentAssertions;
using PR_Bot.Analyzers.Gemini;
using PR_Bot.Core.Models;
using Xunit;

public class GeminiReviewAnalyzerTests
{
    private readonly GeminiReviewAnalyzer _analyzer = new(new GeminiApiClient(new HttpClient()));

    [Fact]
    public void ParseGeminiResponse_ShouldParseStandardJson()
    {
        // Arrange
        var json = """
        {
          "summary": "Looks good with minor concerns.",
          "findings": [
            {
              "file": "src/Controllers/UserController.cs",
              "line": 42,
              "severity": "error",
              "message": "Potential SQL injection in RawQuery string concatenation."
            },
            {
              "file": "src/Services/AuthService.cs",
              "line": 15,
              "severity": "warning",
              "message": "Missing null check on user credentials payload."
            }
          ]
        }
        """;

        // Act
        var (comments, summary) = _analyzer.ParseGeminiResponse(json);

        // Assert
        summary.Should().Be("Looks good with minor concerns.");
        comments.Should().HaveCount(2);

        var first = comments[0];
        first.FilePath.Should().Be("src/Controllers/UserController.cs");
        first.Line.Should().Be(42);
        first.Severity.Should().Be(AnalyzerSeverity.Error);
        first.Body.Should().Contain("Potential SQL injection");

        var second = comments[1];
        second.Severity.Should().Be(AnalyzerSeverity.Warning);
    }

    [Fact]
    public void ParseGeminiResponse_ShouldHandleMarkdownCodeFences()
    {
        // Arrange (Gemini sometimes wraps JSON in ```json ... ```)
        var json = """
        ```json
        {
          "summary": "Clean code.",
          "findings": [
            {
              "file": "Program.cs",
              "line": 1,
              "severity": "info",
              "message": "Consider using top-level statements."
            }
          ]
        }
        ```
        """;

        // Act
        var (comments, summary) = _analyzer.ParseGeminiResponse(json);

        // Assert
        summary.Should().Be("Clean code.");
        comments.Should().ContainSingle();
        comments[0].Severity.Should().Be(AnalyzerSeverity.Info);
    }

    [Fact]
    public void BuildDiffChunks_ShouldSplitLargeDiffsAccurately()
    {
        // Arrange
        var context = new PullRequestContext
        {
            Owner = "test",
            Repo = "test",
            PullRequestNumber = 1,
            Title = "Large PR",
            Author = "author",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "File1.cs",
                    Status = "modified",
                    Additions = 10,
                    Deletions = 0,
                    Changes = 10,
                    Patch = new string('A', 300)
                },
                new PullRequestFileChange
                {
                    FileName = "File2.cs",
                    Status = "modified",
                    Additions = 10,
                    Deletions = 0,
                    Changes = 10,
                    Patch = new string('B', 300)
                }
            ]
        };

        // Act (split chunks with max size 350)
        var chunks = GeminiReviewAnalyzer.BuildDiffChunks(context, maxChunkSize: 350);

        // Assert
        chunks.Should().HaveCount(2);
        chunks[0].Should().Contain("File1.cs");
        chunks[1].Should().Contain("File2.cs");
    }
}
