namespace PR_Bot.Core.Tests.Services;

using FluentAssertions;
using PR_Bot.Core.Services;
using Xunit;

public class PatchResolutionExtractorTests
{
    private readonly PatchResolutionExtractor _extractor = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractResolutionDiff_NullOrWhitespacePatch_ReturnsNull(string? patch)
    {
        var result = _extractor.ExtractResolutionDiff(patch, 10);
        result.Should().BeNull();
    }

    [Fact]
    public void ExtractResolutionDiff_ValidSingleHunk_ReturnsDiffLines()
    {
        // Arrange
        var patch = """
            @@ -40,6 +40,8 @@ namespace PR_Bot;
             public class AuthService
             {
            -    var query = "SELECT * FROM Users WHERE Id = " + id;
            +    var query = "SELECT * FROM Users WHERE Id = @Id";
            +    cmd.Parameters.AddWithValue("@Id", id);
                 return query;
             }
            """;

        // Act
        var result = _extractor.ExtractResolutionDiff(patch, 41);

        // Assert
        result.Should().NotBeNull();
        result.Should().Contain("@@ -40,6 +40,8 @@");
        result.Should().Contain("+    var query = \"SELECT * FROM Users WHERE Id = @Id\";");
        result.Should().Contain("+    cmd.Parameters.AddWithValue(\"@Id\", id);");
    }

    [Fact]
    public void ExtractResolutionDiff_MultiHunkPatch_SelectsCorrectHunkForTargetLine()
    {
        // Arrange
        var patch = """
            @@ -10,4 +10,4 @@ public void M1()
            -    old1();
            +    new1();
             }
            @@ -80,5 +80,6 @@ public void M2()
                 context.Open();
            -    ExecuteUnsafe();
            +    ExecuteSafe();
            +    AuditLog();
                 return;
             }
            """;

        // Act - Target line in second hunk
        var result = _extractor.ExtractResolutionDiff(patch, 82);

        // Assert
        result.Should().NotBeNull();
        result.Should().Contain("@@ -80,5 +80,6 @@");
        result.Should().Contain("+    ExecuteSafe();");
        result.Should().NotContain("new1();");
    }

    [Fact]
    public void ExtractResolutionDiff_TargetLineOffset_SelectsClosestHunk()
    {
        // Arrange
        var patch = """
            @@ -25,4 +25,5 @@
             int a = 1;
            +int b = 2;
             return a + b;
            """;

        // Act - line 35 is closest to the hunk starting at 25
        var result = _extractor.ExtractResolutionDiff(patch, 35);

        // Assert
        result.Should().NotBeNull();
        result.Should().Contain("+int b = 2;");
    }
}
