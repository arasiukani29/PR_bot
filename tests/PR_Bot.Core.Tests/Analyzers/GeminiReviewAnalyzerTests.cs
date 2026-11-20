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
        comments[0].Body.Should().Contain("[HIGH]");
        comments[1].Severity.Should().Be(AnalyzerSeverity.Warning);
        comments[1].Body.Should().Contain("[MEDIUM]");
        comments[2].Severity.Should().Be(AnalyzerSeverity.Info);
        comments[2].Body.Should().Contain("[LOW]");
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
        message.Should().Contain("Return ALL findings");
        message.Should().Contain("single review pass");
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

    [Fact]
    public void ParseGeminiResponse_ShouldHandleUnescapedNewlinesInCodeBlocks()
    {
        // Arrange: Raw JSON with unescaped 0x0A newlines inside string values (the exact error from Gemini)
        var jsonWithRawNewlines = "{\n" +
            "  \"summary\": \"Initial dummy auth\",\n" +
            "  \"findings\": [\n" +
            "    {\n" +
            "      \"file\": \"Controllers/AuthController.cs\",\n" +
            "      \"line\": 33,\n" +
            "      \"priority\": \"high\",\n" +
            "      \"message\": \"The controller returns NotFound.\n\n```csharp\nif (!result.Success)\n{\n  return Unauthorized();\n}\n```\"\n" +
            "    }\n" +
            "  ]\n" +
            "}";

        // Act
        var (comments, summary) = _analyzer.ParseGeminiResponse(jsonWithRawNewlines);

        // Assert
        summary.Should().Be("Initial dummy auth");
        comments.Should().ContainSingle();
        comments[0].FilePath.Should().Be("Controllers/AuthController.cs");
        comments[0].Line.Should().Be(33);
        comments[0].Severity.Should().Be(AnalyzerSeverity.Error);
        comments[0].Body.Should().Contain("Unauthorized()");
    }

    [Fact]
    public void FindClosestDiffPosition_ShouldSnapToNearestHunkLine_WhenTargetLineOutsideExactDiff()
    {
        // Arrange: Diff only contains lines 30 to 35
        var patch = """
            @@ -30,2 +30,4 @@
             int a = 1;
            +int b = 2;
            +int c = 3;
             int d = 4;
            """;

        // Act: Gemini points to line 25 (outside hunk) or line 50
        var closestTo25 = GeminiReviewAnalyzer.FindClosestDiffPosition(patch, 25);
        var closestTo50 = GeminiReviewAnalyzer.FindClosestDiffPosition(patch, 50);

        // Assert: Nearest diff line should be found instead of returning null and losing the comment
        closestTo25.Should().NotBeNull();
        closestTo50.Should().NotBeNull();
    }

    [Fact]
    public void BuildDiffChunks_ShouldSplitByMaxFilesPerChunk_WhenConfigured()
    {
        // Arrange: 5 changed files with MaxFilesPerChunk = 2
        var files = Enumerable.Range(1, 5).Select(i => new PullRequestFileChange
        {
            FileName = $"Service{i}.cs",
            Status = "modified",
            Additions = 1,
            Deletions = 0,
            Changes = 1,
            Patch = "@@ -1,1 +1,2 @@\n a\n+b\n"
        }).ToList();

        var context = new PullRequestContext
        {
            Owner = "owner",
            Repo = "repo",
            PullRequestNumber = 1,
            Title = "Multi-file PR",
            Author = "author",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha",
            ChangedFiles = files
        };

        var options = new GeminiOptions
        {
            MaxChunkCharacters = 100000,
            MaxFilesPerChunk = 2
        };

        // Act
        var chunks = GeminiReviewAnalyzer.BuildDiffChunks(context, 100000, options);

        // Assert: 5 files / 2 per chunk = 3 chunks (2, 2, 1)
        chunks.Should().HaveCount(3);
        chunks[0].Should().Contain("Service1.cs").And.Contain("Service2.cs");
        chunks[1].Should().Contain("Service3.cs").And.Contain("Service4.cs");
        chunks[2].Should().Contain("Service5.cs");
    }

    [Fact]
    public void ParseGeminiResponse_ShouldParseInvertedJson_WithFindingsBeforeSummary()
    {
        // Arrange (Inverted JSON where findings array comes before summary)
        var json = """
        {
          "findings": [
            {
              "file": "src/Security/TokenService.cs",
              "line": 45,
              "priority": "high",
              "message": "Potential hardcoded JWT secret key."
            },
            {
              "file": "src/Controllers/OrdersController.cs",
              "line": 88,
              "priority": "medium",
              "message": "Missing null check on request.Items list."
            },
            {
              "file": "src/Controllers/OrdersController.cs",
              "line": 105,
              "priority": "low",
              "message": "Consider using pattern matching."
            }
          ],
          "summary": "Audited token service and orders controller with 3 findings identified."
        }
        """;

        // Act
        var (comments, summary) = _analyzer.ParseGeminiResponse(json);

        // Assert
        summary.Should().Be("Audited token service and orders controller with 3 findings identified.");
        comments.Should().HaveCount(3);
        comments[0].FilePath.Should().Be("src/Security/TokenService.cs");
        comments[0].Line.Should().Be(45);
        comments[0].Severity.Should().Be(AnalyzerSeverity.Error);
        comments[0].Body.Should().Contain("[HIGH]");

        comments[1].FilePath.Should().Be("src/Controllers/OrdersController.cs");
        comments[1].Line.Should().Be(88);
        comments[1].Severity.Should().Be(AnalyzerSeverity.Warning);
        comments[1].Body.Should().Contain("[MEDIUM]");

        comments[2].FilePath.Should().Be("src/Controllers/OrdersController.cs");
        comments[2].Line.Should().Be(105);
        comments[2].Severity.Should().Be(AnalyzerSeverity.Info);
        comments[2].Body.Should().Contain("[LOW]");
    }

    [Fact]
    public void BuildDiffChunks_ShouldBundleAllFilesInSingleChunk_WhenUsingDefaultOptions()
    {
        // Arrange: 3 changed files with default GeminiOptions (MaxFilesPerChunk = 50)
        var files = Enumerable.Range(1, 3).Select(i => new PullRequestFileChange
        {
            FileName = $"Component{i}.cs",
            Status = "modified",
            Additions = 2,
            Deletions = 0,
            Changes = 2,
            Patch = "@@ -1,1 +1,3 @@\n a\n+b\n+c\n"
        }).ToList();

        var context = new PullRequestContext
        {
            Owner = "owner",
            Repo = "repo",
            PullRequestNumber = 10,
            Title = "Multi-file unified chunk",
            Author = "author",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha",
            ChangedFiles = files
        };

        // Act (using default options where all files bundle into 1 unified call)
        var chunks = GeminiReviewAnalyzer.BuildDiffChunks(context, maxChunkSize: 500000);

        // Assert: 3 files with default options should produce 1 unified chunk containing all files and the manifest
        chunks.Should().HaveCount(1);
        chunks[0].Should().Contain("FILES TO AUDIT IN THIS CHUNK (Total: 3 files)");
        chunks[0].Should().Contain("CRITICAL MULTI-FILE INSTRUCTION");
        chunks[0].Should().Contain("[FILE 1/3] Component1.cs");
        chunks[0].Should().Contain("[FILE 2/3] Component2.cs");
        chunks[0].Should().Contain("[FILE 3/3] Component3.cs");
    }

    [Fact]
    public void CodeReviewPrompt_ShouldContainFivePillarsAndZeroWithholdingRule()
    {
        // Assert SystemInstruction contains 5 review pillars and zero-withholding rule
        var instruction = PR_Bot.Analyzers.Gemini.Prompts.CodeReviewPrompt.SystemInstruction;
        instruction.Should().Contain("ZERO-WITHHOLDING POLICY");
        instruction.Should().Contain("Pillar 1 (Security");
        instruction.Should().Contain("Pillar 2 (Reliability");
        instruction.Should().Contain("Pillar 3 (Input Validation");
        instruction.Should().Contain("Pillar 4 (Performance");
        instruction.Should().Contain("Pillar 5 (Maintainability");

        // Assert UserMessage reinforces the rule
        var userMsg = PR_Bot.Analyzers.Gemini.Prompts.CodeReviewPrompt.BuildUserMessage("Title", "author", "diff");
        userMsg.Should().Contain("ZERO-WITHHOLDING RULE");
        userMsg.Should().Contain("5 review pillars");
    }

    [Fact]
    public void CodeReviewPrompt_ShouldContainAntiPingPongAndUniversalPriorityRules()
    {
        var instruction = PR_Bot.Analyzers.Gemini.Prompts.CodeReviewPrompt.SystemInstruction;

        // Anti-speculation and anti-bikeshedding rules
        instruction.Should().Contain("NO SPECULATIVE \"WHAT-IF\" CRITIQUES");
        instruction.Should().Contain("NO BIKESHEDDING OR CONTRADICTORY PING-PONGING");
        instruction.Should().Contain("THE \"PROVE THE BUG\" TEST");

        // Priority and new code rules
        instruction.Should().Contain("critical\" (Critical Blocker");
        instruction.Should().Contain("high\" (High Priority");
        instruction.Should().Contain("medium\" (Medium Priority");
        instruction.Should().Contain("low\" (Low Priority");

        // UserMessage instructions
        var userMsg = PR_Bot.Analyzers.Gemini.Prompts.CodeReviewPrompt.BuildUserMessage("Title", "author", "diff");
        userMsg.Should().Contain("4-TIER PRIORITY HIERARCHY");
        userMsg.Should().Contain("NO SPECULATION OR BIKESHEDDING");
    }

    [Fact]
    public void AdjustMeaningfulLine_ShouldSnapToPreviousCodeStatement_WhenLineIsClosingBrace()
    {
        // Arrange: Line 59 is a closing brace '}', line 58 is a return statement
        var code = """
        namespace Services;
        public class AuthService
        {
            public bool SecureStringEquals(string a, string b)
            {
                if (a.Length != b.Length) return false;
                return a == b;
            }
        }
        """;
        var lines = code.Split('\n');
        // Let's create a file change with 59 lines where line 59 is '}' and line 58 is code
        var fullContent = string.Join("\n", Enumerable.Range(1, 57).Select(i => $"// Line {i}"))
                          + "\n    return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);"
                          + "\n}";

        var file = new PullRequestFileChange
        {
            FileName = "Services/AuthService.cs",
            Status = "modified",
            Additions = 2,
            Deletions = 0,
            Changes = 2,
            FullContent = fullContent
        };

        // Act (target line is 59, which is '}')
        var adjusted = GeminiReviewAnalyzer.AdjustMeaningfulLine(file, targetLine: 59);

        // Assert: Should snap upward to line 58
        adjusted.Should().Be(58);
    }

    [Fact]
    public void AdjustMeaningfulLine_ShouldSnapToMethodSignature_WhenLineIsOpeningBrace()
    {
        // Arrange: Line 19 is method signature, line 20 is '{'
        var fullContent = string.Join("\n", Enumerable.Range(1, 18).Select(i => $"// Line {i}"))
                          + "\npublic async Task<IActionResult> Login([FromBody] LoginRequestDto request)"
                          + "\n{"
                          + "\n    return Ok();"
                          + "\n}";

        var file = new PullRequestFileChange
        {
            FileName = "Controllers/AuthController.cs",
            Status = "modified",
            Additions = 4,
            Deletions = 0,
            Changes = 4,
            FullContent = fullContent
        };

        // Act (target line is 20, which is '{')
        var adjusted = GeminiReviewAnalyzer.AdjustMeaningfulLine(file, targetLine: 20);

        // Assert: Should snap upward to line 19
        adjusted.Should().Be(19);
    }

    [Fact]
    public void ParseGeminiResponse_WithContext_ShouldAdjustBraceLinesToMeaningfulCode()
    {
        // Arrange
        var fullContent = "line 1\npublic void Method()\n{\n    DoSomething();\n}";
        var patch = "@@ -1,5 +1,5 @@\n line 1\n public void Method()\n {\n+    DoSomething();\n }";

        var context = new PullRequestContext
        {
            Owner = "owner",
            Repo = "repo",
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
                    FileName = "TestService.cs",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 0,
                    Changes = 1,
                    Patch = patch,
                    FullContent = fullContent
                }
            ]
        };

        var json = """
        {
          "findings": [
            {
              "file": "TestService.cs",
              "line": 5,
              "priority": "high",
              "message": "Defect at closing brace."
            }
          ],
          "summary": "Review summary"
        }
        """;

        // Act
        var (comments, _) = _analyzer.ParseGeminiResponse(json, context);

        // Assert: Line 5 is '}', should snap to line 4 ('DoSomething();')
        comments.Should().ContainSingle();
        comments[0].Line.Should().Be(4);
    }

    [Fact]
    public void ParseGeminiResponse_WithRichStructuredFields_ShouldBuildRichGitHubSuggestionComment()
    {
        // Arrange
        var json = """
        {
          "findings": [
            {
              "file": "Controllers/AuthController.cs",
              "line": 38,
              "priority": "critical",
              "title": "Logging Sensitive Information (Plaintext Password PII)",
              "category": "Security",
              "message": "The application logs the user's plaintext password directly via structured logging (_logger.LogInformation(... request.Password)).",
              "why_it_matters": "Logging credentials exposes sensitive secrets in log management systems, violating security compliance standards and leading to credential theft.",
              "suggested_fix": "_logger.LogInformation(\"Processing login attempt for user '{Username}'.\", request.Username);"
            }
          ],
          "summary": "Security defect detected in AuthController."
        }
        """;

        // Act
        var (comments, summary) = _analyzer.ParseGeminiResponse(json);

        // Assert
        summary.Should().Be("Security defect detected in AuthController.");
        comments.Should().ContainSingle();

        var comment = comments[0];
        comment.Title.Should().Be("Logging Sensitive Information (Plaintext Password PII)");
        comment.Category.Should().Be("Security");
        comment.WhyItMatters.Should().Contain("violating security compliance standards");
        comment.SuggestedFix.Should().Be("_logger.LogInformation(\"Processing login attempt for user '{Username}'.\", request.Username);");

        // Verify the rendered Body contains the exact UI elements shown in GitHub comments
        comment.Body.Should().Contain("🚨 **[CRITICAL]** Logging Sensitive Information (Plaintext Password PII)");
        comment.Body.Should().Contain("> Category: `Security`");
        comment.Body.Should().Contain("**Problem:**");
        comment.Body.Should().Contain("The application logs the user's plaintext password directly");
        comment.Body.Should().Contain("**Why It Matters:**");
        comment.Body.Should().Contain("violating security compliance standards");
        comment.Body.Should().Contain("**Suggested Fix:**");
        comment.Body.Should().Contain("```suggestion");
        comment.Body.Should().Contain("_logger.LogInformation(\"Processing login attempt for user '{Username}'.\", request.Username);");
    }

    [Fact]
    public void ParseGeminiResponse_WithIdenticalSuggestedFix_ShouldDiscardSuggestionBlock()
    {
        // Arrange: Line 28 contains exact same text as the suggestion
        var targetCode = "const primaryRole = this.currentUser?.roles?.[0] || 'User';";
        var patch = $"@@ -28,1 +28,1 @@\n+{targetCode}";
        var fullContent = string.Join("\n", Enumerable.Range(1, 27).Select(i => $"// Line {i}")) + "\n  " + targetCode;

        var context = new PullRequestContext
        {
            Owner = "owner",
            Repo = "repo",
            PullRequestNumber = 5,
            Title = "Test identical fix",
            Author = "author",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha",
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "src/app/login/login.component.ts",
                    Status = "modified",
                    Additions = 1,
                    Deletions = 0,
                    Changes = 1,
                    Patch = patch,
                    FullContent = fullContent
                }
            ]
        };

        var json = $$"""
        {
          "findings": [
            {
              "file": "src/app/login/login.component.ts",
              "line": 28,
              "priority": "medium",
              "title": "Null Reference Access on Uninitialized User Object",
              "category": "Reliability",
              "message": "Accessing this.currentUser?.roles?.[0] when currentUser is initialized to null results in undefined role assignment.",
              "why_it_matters": "Users logging in will always fall back to 'User' role.",
              "suggested_fix": "{{targetCode}}"
            }
          ],
          "summary": "Review summary."
        }
        """;

        // Act
        var (comments, _) = _analyzer.ParseGeminiResponse(json, context);

        // Assert: The suggested fix is identical to original line 28, so it MUST be discarded!
        comments.Should().ContainSingle();
        var comment = comments[0];
        comment.SuggestedFix.Should().BeNull();
        comment.Body.Should().NotContain("```suggestion");
        comment.Body.Should().Contain("Accessing this.currentUser?.roles?.[0]");
    }

    [Fact]
    public void DeduplicateComments_SameLineMultipleFindings_ShouldKeepHighestCategoryPrecedence()
    {
        // Arrange: Two findings on the exact same file and line
        // One is labeled 'Maintainability' and the other is 'Security'
        var comments = new List<ReviewComment>
        {
            new ReviewComment
            {
                FilePath = "Controllers/AuthController.cs",
                Line = 38,
                Body = "Maintainability finding",
                Severity = AnalyzerSeverity.Error,
                AnalyzerName = "Gemini AI",
                Category = "Maintainability"
            },
            new ReviewComment
            {
                FilePath = "Controllers/AuthController.cs",
                Line = 38,
                Body = "Security finding",
                Severity = AnalyzerSeverity.Error,
                AnalyzerName = "Gemini AI",
                Category = "Security"
            }
        };

        // Act
        var deduplicated = GeminiReviewAnalyzer.DeduplicateComments(comments);

        // Assert: Exactly one finding remains, and it MUST be the Security one (Priority 1)
        deduplicated.Should().ContainSingle();
        deduplicated[0].Category.Should().Be("Security");
        deduplicated[0].Body.Should().Be("Security finding");
    }

    [Fact]
    public void ParseGeminiResponse_WithModelName_ShouldIncludeModelInAnalyzerName()
    {
        // Arrange
        var json = """
        {
          "findings": [
            {
              "file": "Service.cs",
              "line": 10,
              "priority": "high",
              "title": "Bug",
              "category": "Reliability",
              "message": "Null ref."
            }
          ],
          "summary": "Done"
        }
        """;

        // Act
        var (comments, _) = _analyzer.ParseGeminiResponse(json, modelName: "gemini-flash-lite-latest");

        // Assert
        comments.Should().ContainSingle();
        comments[0].AnalyzerName.Should().Be("Gemini AI (gemini-flash-lite-latest)");
    }

    [Fact]
    public void CodeReviewPrompt_ShouldContainCategoryPrecedenceAndPIIUnderSecurity()
    {
        // Assert
        var instruction = PR_Bot.Analyzers.Gemini.Prompts.CodeReviewPrompt.SystemInstruction;
        instruction.Should().Contain("STRICT CATEGORY PRECEDENCE & DISAMBIGUATION");
        instruction.Should().Contain("passwords or sensitive PII in logs (CWE-532)");
        instruction.Should().Contain("Security\" (Priority 1)");
        instruction.Should().Contain("Reliability\" (Priority 2)");

        var userMsg = PR_Bot.Analyzers.Gemini.Prompts.CodeReviewPrompt.BuildUserMessage("Title", "author", "diff");
        userMsg.Should().Contain("STRICT CATEGORY PRECEDENCE");
        userMsg.Should().Contain("MULTI-FILE AUDIT");
    }

    [Fact]
    public void ParseGeminiResponse_WithFourPriorityTiers_ShouldMapAccurately()
    {
        // Arrange
        var json = """
        {
          "summary": "Full review with 4 distinct priority tiers.",
          "findings": [
            {
              "file": "src/Auth.cs",
              "line": 10,
              "priority": "critical",
              "title": "Plaintext Password Logging",
              "category": "Security",
              "message": "Logs sensitive secrets."
            },
            {
              "file": "src/Payment.cs",
              "line": 20,
              "priority": "high",
              "title": "Race Condition in Checkout",
              "category": "Reliability",
              "message": "Double charge risk."
            },
            {
              "file": "src/User.cs",
              "line": 30,
              "priority": "medium",
              "title": "Missing Range Validation",
              "category": "Input Validation",
              "message": "Age can be negative."
            },
            {
              "file": "src/Helper.cs",
              "line": 40,
              "priority": "low",
              "title": "Use String Interpolation",
              "category": "Maintainability",
              "message": "Optional style improvement."
            }
          ]
        }
        """;

        // Act
        var (comments, summary) = _analyzer.ParseGeminiResponse(json);

        // Assert
        summary.Should().Be("Full review with 4 distinct priority tiers.");
        comments.Should().HaveCount(4);

        // Tier 1: Critical
        comments[0].Severity.Should().Be(AnalyzerSeverity.Critical);
        comments[0].Body.Should().Contain("🚨 **[CRITICAL]**");

        // Tier 2: High
        comments[1].Severity.Should().Be(AnalyzerSeverity.High);
        comments[1].Body.Should().Contain("🔴 **[HIGH]**");

        // Tier 2: Medium
        comments[2].Severity.Should().Be(AnalyzerSeverity.Medium);
        comments[2].Body.Should().Contain("⚠️ **[MEDIUM]**");

        // Tier 4: Low
        comments[3].Severity.Should().Be(AnalyzerSeverity.Low);
        comments[3].Body.Should().Contain("💡 **[LOW]**");
    }

    [Fact]
    public void ParseGeminiResponse_WithPerFileReviewsSchema_ShouldExtractFindingsFromAllFiles()
    {
        // Arrange: Approach 2 schema where findings are nested under file_reviews per file
        var json = """
        {
          "file_reviews": [
            {
              "file": "Controllers/AuthController.cs",
              "findings": [
                {
                  "line": 40,
                  "priority": "critical",
                  "title": "Plaintext Password Logging",
                  "category": "Security",
                  "message": "Logs user password."
                },
                {
                  "line": 59,
                  "priority": "critical",
                  "title": "Remote Code Execution",
                  "category": "Security",
                  "message": "Command injection in diagnostics."
                }
              ]
            },
            {
              "file": "Services/AuthService.cs",
              "findings": [
                {
                  "line": 25,
                  "priority": "critical",
                  "title": "Hardcoded Backdoor Credentials",
                  "category": "Security",
                  "message": "MasterAdminSecret2026! bypasses password check."
                },
                {
                  "line": 45,
                  "priority": "high",
                  "title": "Cryptographically Weak PRNG",
                  "category": "Security",
                  "message": "System.Random used for session tokens."
                }
              ]
            }
          ],
          "summary": "Audited AuthController and AuthService. Found 4 total vulnerabilities across both files."
        }
        """;

        // Act
        var (comments, summary) = _analyzer.ParseGeminiResponse(json);

        // Assert: Both files MUST have their findings extracted! Total = 4
        summary.Should().Be("Audited AuthController and AuthService. Found 4 total vulnerabilities across both files.");
        comments.Should().HaveCount(4);

        // AuthController findings
        comments.Should().Contain(c => c.FilePath == "Controllers/AuthController.cs" && c.Line == 40 && c.Severity == AnalyzerSeverity.Critical);
        comments.Should().Contain(c => c.FilePath == "Controllers/AuthController.cs" && c.Line == 59 && c.Severity == AnalyzerSeverity.Critical);

        // AuthService findings (no longer skipped!)
        comments.Should().Contain(c => c.FilePath == "Services/AuthService.cs" && c.Line == 25 && c.Severity == AnalyzerSeverity.Critical);
        comments.Should().Contain(c => c.FilePath == "Services/AuthService.cs" && c.Line == 45 && c.Severity == AnalyzerSeverity.High);
    }

    [Fact]
    public void FindDiffPosition_ShouldAccuratelyMatchGitHubPositionsWithoutHunkOffset()
    {
        // Patch with 3 added lines in a single hunk
        var patch = """
            @@ -0,0 +1,3 @@
            +declare module "foo" {
            +  export function Component(options: any): ClassDecorator;
            +}
            """;

        // GitHub API: Line 1 after hunk header is position 1, line 2 is position 2, line 3 is position 3
        var posLine1 = GeminiReviewAnalyzer.FindDiffPosition(patch, 1);
        var posLine2 = GeminiReviewAnalyzer.FindDiffPosition(patch, 2);
        var posLine3 = GeminiReviewAnalyzer.FindDiffPosition(patch, 3);

        posLine1.Should().Be(1);
        posLine2.Should().Be(2);
        posLine3.Should().Be(3);
    }

    [Fact]
    public void AdjustMeaningfulLine_ShouldScanUpwardWhenTargetingClosingBrace()
    {
        var file = new PullRequestFileChange
        {
            FileName = "src/types.d.ts",
            Status = "added",
            Additions = 3,
            Deletions = 0,
            Changes = 3,
            FullContent = """
                declare module "foo" {
                  export function Component(options: any): ClassDecorator;
                }
                """
        };

        // When AI targets line 3 (which is solitary brace '}'), adjust upward to line 2 (the function declaration)
        var adjusted = GeminiReviewAnalyzer.AdjustMeaningfulLine(file, 3);
        adjusted.Should().Be(2);
    }

    [Fact]
    public void ParseFindingElement_ShouldPreserveIndentationAndTargetCorrectLine()
    {
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
            ChangedFiles =
            [
                new PullRequestFileChange
                {
                    FileName = "src/types.d.ts",
                    Status = "added",
                    Additions = 3,
                    Deletions = 0,
                    Changes = 3,
                    Patch = """
                        @@ -0,0 +1,3 @@
                        +declare module "foo" {
                        +  export function Component(options: any): ClassDecorator;
                        +}
                        """,
                    FullContent = """
                        declare module "foo" {
                          export function Component(options: any): ClassDecorator;
                        }
                        """
                }
            ]
        };

        var json = """
            {
              "findings": [
                {
                  "file": "src/types.d.ts",
                  "line": 3,
                  "priority": "low",
                  "title": "Avoid any typing",
                  "message": "Use strict types",
                  "suggested_fix": "export function Component(options: Record<string, unknown>): ClassDecorator;"
                }
              ]
            }
            """;

        var (comments, _) = _analyzer.ParseGeminiResponse(json, context);

        comments.Should().ContainSingle();
        var comment = comments[0];

        // Should be adjusted from brace Line 3 to declaration Line 2!
        comment.Line.Should().Be(2);
        comment.DiffPosition.Should().Be(2);

        // Leading 2 spaces indentation from Line 2 must be preserved in suggested_fix
        comment.Body.Should().Contain("  export function Component(options: Record<string, unknown>): ClassDecorator;");
        comment.Body.Should().Contain("```suggestion");
    }

    [Fact]
    public void ParseGeminiResponse_WithStartLineAndLine_ShouldPopulateStartLine()
    {
        // Arrange
        var json = """
        {
          "findings": [
            {
              "file": "src/app/login.component.html",
              "line": 12,
              "start_line": 8,
              "priority": "low",
              "title": "Add autocomplete attribute for username",
              "category": "Input Validation",
              "message": "Adding autocomplete='username' enables browser password managers to accurately autofill the field.",
              "suggested_fix": "  type=\"text\"\n  id=\"username\"\n  name=\"username\"\n  [(ngModel)]=\"username\"\n  autocomplete=\"username\"\n  required"
            }
          ],
          "summary": "Added input autocomplete attributes."
        }
        """;

        // Act
        var (comments, summary) = _analyzer.ParseGeminiResponse(json);

        // Assert
        summary.Should().Be("Added input autocomplete attributes.");
        comments.Should().ContainSingle();
        var comment = comments[0];
        comment.FilePath.Should().Be("src/app/login.component.html");
        comment.StartLine.Should().Be(8);
        comment.Line.Should().Be(12);
        comment.SuggestedFix.Should().Contain("autocomplete=\"username\"");
        comment.Body.Should().Contain("```suggestion");
    }

    [Fact]
    public void ParseGeminiResponse_WithInvalidStartLine_ShouldFallbackToSingleLine()
    {
        // Arrange: start_line is greater than or equal to line
        var json = """
        {
          "findings": [
            {
              "file": "src/Service.cs",
              "line": 10,
              "start_line": 15,
              "priority": "medium",
              "message": "Invalid line range where start > end."
            },
            {
              "file": "src/Service.cs",
              "line": 20,
              "start_line": 20,
              "priority": "low",
              "message": "Single line disguised as range."
            }
          ],
          "summary": "Done"
        }
        """;

        // Act
        var (comments, _) = _analyzer.ParseGeminiResponse(json);

        // Assert: Both should have StartLine == null
        comments.Should().HaveCount(2);
        comments[0].StartLine.Should().BeNull();
        comments[0].Line.Should().Be(10);
        comments[1].StartLine.Should().BeNull();
        comments[1].Line.Should().Be(20);
    }

    [Fact]
    public void BuildReviewMarkdown_WithMultilineComment_ShouldDisplayLineRangeInSummary()
    {
        // Arrange
        var context = new PullRequestContext
        {
            Owner = "owner",
            Repo = "repo",
            PullRequestNumber = 5,
            Title = "UI improvements",
            Author = "author",
            BaseBranch = "main",
            HeadBranch = "feature",
            HeadSha = "sha",
            ChangedFiles = [new PullRequestFileChange { FileName = "login.html", Status = "modified", Additions = 5, Deletions = 0, Changes = 5 }]
        };

        var result = new ReviewResult
        {
            Comments =
            [
                new ReviewComment
                {
                    FilePath = "login.html",
                    StartLine = 8,
                    Line = 12,
                    Title = "Add autocomplete attribute",
                    Category = "Input Validation",
                    Body = "Improve accessibility and autofill.",
                    Severity = AnalyzerSeverity.Info,
                    AnalyzerName = "Gemini AI"
                }
            ]
        };

        // Act
        var markdown = PR_Bot.Core.Services.GitHubService.BuildReviewMarkdown(context, result);

        // Assert: Markdown header displays (Lines 8-12) instead of (Line 12)
        markdown.Should().Contain("`login.html` (Lines 8-12)");
    }
}
