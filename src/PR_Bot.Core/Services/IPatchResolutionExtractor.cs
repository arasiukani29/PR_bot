namespace PR_Bot.Core.Services;

/// <summary>
/// Service responsible for extracting the patch/diff snippet that resolved a previously reported defect.
/// </summary>
public interface IPatchResolutionExtractor
{
    /// <summary>
    /// Extracts the unified diff snippet around the specified target line from a file's Git patch.
    /// </summary>
    /// <param name="patch">The Git unified diff patch for the file.</param>
    /// <param name="targetLine">The line number where the defect was previously reported.</param>
    /// <returns>A formatted diff snippet showing the code changes applied, or null if no diff was found.</returns>
    string? ExtractResolutionDiff(string? patch, int targetLine);
}
