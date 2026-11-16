namespace PR_Bot.Core.Analyzers;

using System.Text.RegularExpressions;
using PR_Bot.Core.Models;

/// <summary>
/// Scans added lines in pull request diffs for common security vulnerabilities and leaked secrets.
/// </summary>
public sealed class SecurityAnalyzer : IReviewAnalyzer
{
    private static readonly Regex HunkRegex = new(@"^@@\s+-\d+(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s+@@", RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Message, AnalyzerSeverity Severity)[] Rules =
    [
        (new(@"-----BEGIN\s+(RSA\s+|EC\s+|DSA\s+|OPENSSH\s+)?PRIVATE\s+KEY-----", RegexOptions.Compiled | RegexOptions.IgnoreCase),
         "🚨 **Private Key Detected!** Private cryptographic keys must never be committed to source control. Revoke and rotate this key immediately.",
         AnalyzerSeverity.Error),

        (new(@"(?i)([""']?(?:api[_-]?key|secret|access[_-]?token|auth[_-]?token|private[_-]?key)[""']?\s*[:=]\s*[""'][A-Za-z0-9_\-+/]{16,}[""'])", RegexOptions.Compiled),
         "🔒 **Possible Hardcoded Secret Detected.** Store credentials in environment variables, GitHub Secrets, or a secret manager.",
         AnalyzerSeverity.Error),

        (new(@"(?i)(password|passwd|pwd)\s*[:=]\s*[""'][^""']{6,}[""']", RegexOptions.Compiled),
         "🔒 **Hardcoded Password Detected.** Avoid embedding plain-text passwords in code.",
         AnalyzerSeverity.Error),

        (new(@"(?i)(?:User\s*Id|UID|Password|PWD)\s*=\s*[^;""']+\s*;.*(?:User\s*Id|UID|Password|PWD)\s*=", RegexOptions.Compiled),
         "🔒 **Embedded Database Credentials Detected.** Use Managed Identity or environment configuration for connection strings.",
         AnalyzerSeverity.Error),

        (new(@"(?i)ServerCertificateCustomValidationCallback\s*=\s*.*(?:true|=>\s*true)", RegexOptions.Compiled),
         "🚨 **SSL/TLS Certificate Validation Bypass Detected.** Never disable certificate validation in production code.",
         AnalyzerSeverity.Error),

        (new(@"(?i)\b(eval|exec)\s*\(", RegexOptions.Compiled),
         "⚠️ **Dynamic Execution Risk.** Dynamic code execution can expose systems to code injection attacks.",
         AnalyzerSeverity.Warning),

        (new(@"(?i)http:\/\/(?:api|auth|login|secure|admin)\.", RegexOptions.Compiled),
         "⚠️ **Insecure HTTP Endpoint.** Sensitive service communication should use HTTPS.",
         AnalyzerSeverity.Warning)
    ];

    public string Name => "SecurityAnalyzer";
    public int Order => 5; // Run early

    public Task<IReadOnlyList<ReviewComment>> AnalyzeAsync(
        PullRequestContext context,
        CancellationToken cancellationToken = default)
    {
        var comments = new List<ReviewComment>();

        foreach (var file in context.ChangedFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Patch) || file.Status.Equals("removed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = file.Patch.Split('\n');
            int currentLine = 0;
            int diffPosition = 0;
            bool foundFirstHunk = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');

                var match = HunkRegex.Match(line);
                if (match.Success)
                {
                    foundFirstHunk = true;
                    currentLine = int.Parse(match.Groups[1].Value);
                    diffPosition++;
                    continue;
                }

                if (!foundFirstHunk) continue;
                diffPosition++;

                if (line.StartsWith('+') && !line.StartsWith("+++"))
                {
                    var content = line[1..];

                    foreach (var (pattern, message, severity) in Rules)
                    {
                        if (pattern.IsMatch(content))
                        {
                            comments.Add(new ReviewComment
                            {
                                FilePath = file.FileName,
                                Line = currentLine,
                                DiffPosition = diffPosition,
                                Body = message,
                                Severity = severity,
                                AnalyzerName = Name
                            });
                        }
                    }

                    currentLine++;
                }
                else if (line.StartsWith('-') && !line.StartsWith("---"))
                {
                    // Deleted line
                }
                else if (line.StartsWith(' '))
                {
                    currentLine++;
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ReviewComment>>(comments);
    }
}
