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
    public void ParseGeminiResponse_ShouldParsePriorityFieldWithBadge()
    {
        // Arrange
        var json = """
        {
          "summary": "Review complete.",
          "findings": [
            {
              "file": "src/Security.cs",
              "line": 10,
              "priority": "high",
              "message": "Critical vulnerability found."
            },
            {
              "file": "src/Service.cs",
              "line": 20,
              "priority": "medium",
              "message": "Performance concern."
            },
            {
              "file": "src/Helper.cs",
              "line": 30,
              "priority": "low",
              "message": "Minor suggestion."
            }
          ]
        }
        """;

        // Act
        var (comments, summary) = _analyzer.ParseGeminiResponse(json);

        // Assert
        summary.Should().Be("Review complete.");
        comments.Should().HaveCount(3);
        comments[0].Severity.Should().Be(AnalyzerSeverity.Error);
        comments[0].Body.Should().Contain("[HIGH PRIORITY]");
        comments[1].Severity.Should().Be(AnalyzerSeverity.Warning);
        comments[1].Body.Should().Contain("[MEDIUM PRIORITY]");
        comments[2].Severity.Should().Be(AnalyzerSeverity.Info);
        comments[2].Body.Should().Contain("[LOW PRIORITY]");
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

    [Fact]
    public void BuildUserMessage_ShouldIncludePrDescriptionAndFirstPassInstructions()
    {
        // Arrange
        var title = "Add payment integration";
        var author = "developer1";
        var description = "Please review specifically for concurrency safety and token leaks.";
        var diff = "some diff payload";

        // Act
        var message = PR_Bot.Analyzers.Gemini.Prompts.CodeReviewPrompt.BuildUserMessage(title, author, description, diff);

        // Assert
        message.Should().Contain("Add payment integration");
        message.Should().Contain("@developer1");
        message.Should().Contain("Please review specifically for concurrency safety and token leaks.");
        message.Should().Contain("Return ALL findings on this first review pass");
    }

    [Fact]
    public void BuildFileContext_SmallFile_ShouldIncludeNumberedFullFileContext()
    {
        // Arrange
        var patch = """
            @@ -10,3 +10,4 @@
             var x = 1;
            +var y = 2;
             return x + y;
            """;

        var fullContent = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"// Line {i}"));

        var file = new PullRequestFileChange
        {
            FileName = "src/Calculator.cs",
            Status = "modified",
            Additions = 1,
            Deletions = 0,
            Changes = 1,
            Patch = patch,
            FullContent = fullContent
        };

        var options = new GeminiOptions { MaxFullFileLines = 100 };

        // Act
        var contextStr = GeminiReviewAnalyzer.BuildFileContext(file, options);

        // Assert
        contextStr.Should().Contain("FULL FILE CONTEXT (50 lines - READ-ONLY REFERENCE)");
        contextStr.Should().Contain("CHANGED / ADDED LINES TO REVIEW: [11]");
        contextStr.Should().Contain("   1 | // Line 1");
        contextStr.Should().Contain("  50 | // Line 50");
        contextStr.Should().Contain("GIT UNIFIED DIFF:");
    }

    [Fact]
    public void BuildFileContext_LargeFile_ShouldIncludeWindowedContextWithGapMarkers()
    {
        // Arrange (800 lines file, change at line 500)
        var patch = """
            @@ -500,2 +500,3 @@
             var a = 10;
            +var b = 20;
             var c = 30;
            """;

        var fullContent = string.Join("\n", Enumerable.Range(1, 800).Select(i => $"public int Field{i} = {i};"));

        var file = new PullRequestFileChange
        {
            FileName = "src/LargeService.cs",
            Status = "modified",
            Additions = 1,
            Deletions = 0,
            Changes = 1,
            Patch = patch,
            FullContent = fullContent
        };

        var options = new GeminiOptions
        {
            MaxFullFileLines = 100, // force windowing
            SurroundingContextLines = 5
        };

        // Act
        var contextStr = GeminiReviewAnalyzer.BuildFileContext(file, options);

        // Assert
        contextStr.Should().Contain("WINDOWED FILE CONTEXT (800 lines - Large file");
        contextStr.Should().Contain("CHANGED / ADDED LINES TO REVIEW: [501]");
        // Should include file header (lines 1..30)
        contextStr.Should().Contain("   1 | public int Field1");
        contextStr.Should().Contain("  30 | public int Field30");
        // Should contain gap omission marker
        contextStr.Should().Contain("lines omitted");
        // Should contain surrounding window around line 501 (e.g. line 500..506)
        contextStr.Should().Contain(" 501 | public int Field501");
    }

    [Fact]
    public void GetAddedOrModifiedLines_ShouldExtractAccurateLineNumbersFromPatch()
    {
        // Arrange
        var patch = """
            @@ -1,3 +1,5 @@
             int a = 1;
            +int b = 2;
            +int c = 3;
             int d = 4;
            @@ -20,2 +22,3 @@
             int x = 10;
            +int y = 20;
            """;

        var file = new PullRequestFileChange
        {
            FileName = "Test.cs",
            Status = "modified",
            Additions = 3,
            Deletions = 0,
            Changes = 3,
            Patch = patch
        };

        // Act
        var addedLines = file.GetAddedOrModifiedLines();

        // Assert
        // In first hunk: +new starts at 1. Line 1 is context (' int a = 1').
        // Line 2 is '+int b = 2'. Line 3 is '+int c = 3'.
        // In second hunk: +new starts at 22. Line 22 is context (' int x = 10').
        // Line 23 is '+int y = 20'.
        addedLines.Should().Equal(2, 3, 23);
    }
}
