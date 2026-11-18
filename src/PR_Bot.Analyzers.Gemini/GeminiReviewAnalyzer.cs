namespace PR_Bot.Analyzers.Gemini;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PR_Bot.Analyzers.Gemini.Prompts;
using PR_Bot.Core.Analyzers;
using PR_Bot.Core.Models;

/// <summary>
/// AI-powered code review analyzer powered by Google Gemini.
/// </summary>
public sealed class GeminiReviewAnalyzer : IReviewAnalyzer
{
    private static readonly Regex HunkRegex = new(@"^@@\s+-\d+(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s+@@", RegexOptions.Compiled);

    private readonly GeminiApiClient _client;


    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiReviewAnalyzer> _logger;

    public GeminiReviewAnalyzer(
        GeminiApiClient client,
        IOptions<GeminiOptions>? options = null,
        ILogger<GeminiReviewAnalyzer>? logger = null)
    {
        _client = client;
        _options = options?.Value ?? new GeminiOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GeminiReviewAnalyzer>.Instance;
    }

    public string Name => "GeminiAI";
    public int Order => 50; // Runs after rule-based checks
    public string? Summary { get; private set; }

    public async Task<IReadOnlyList<ReviewComment>> AnalyzeAsync(
        PullRequestContext context,
        CancellationToken cancellationToken = default)
    {
        Summary = null;
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("Gemini API key is not configured. Skipping AI review.");
            return [];
        }

        var chunks = BuildDiffChunks(context, _options.MaxChunkCharacters, _options);
        if (chunks.Count == 0)
        {
            _logger.LogInformation("No modified files with patch diffs to review.");
            return [];
        }

        var candidateModels = new List<string> { _options.Model };
        if (_options.FallbackModels != null)
        {
            candidateModels.AddRange(_options.FallbackModels);
        }
        var modelsToTry = candidateModels
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "Submitting {ChunkCount} diff chunk(s) to Gemini. Primary model: {Model}, Fallbacks: [{Fallbacks}]",
            chunks.Count, _options.Model, string.Join(", ", modelsToTry.Skip(1)));

        var comments = new List<ReviewComment>();
        var summaries = new List<string>();
        string currentWorkingModel = modelsToTry[0];

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            _logger.LogInformation("Analyzing chunk {Index}/{Total} ({Length} chars)...", i + 1, chunks.Count, chunk.Length);

            var userPrompt = CodeReviewPrompt.BuildUserMessage(context.Title, context.Author, context.Description, chunk);
            string? jsonResponse = null;

            var modelQueue = new List<string> { currentWorkingModel };
            modelQueue.AddRange(modelsToTry.Where(m => !m.Equals(currentWorkingModel, StringComparison.OrdinalIgnoreCase)));

            foreach (var model in modelQueue)
            {
                try
                {
                    _logger.LogInformation("Attempting review with Gemini model '{Model}'...", model);

                    jsonResponse = await _client.GenerateJsonAsync(
                        apiKey: _options.ApiKey,
                        model: model,
                        systemInstruction: CodeReviewPrompt.SystemInstruction,
                        userPrompt: userPrompt,
                        temperature: _options.Temperature,
                        maxOutputTokens: _options.MaxOutputTokens,
                        cancellationToken: cancellationToken);

                    if (!string.IsNullOrWhiteSpace(jsonResponse))
                    {
                        currentWorkingModel = model;
                        _logger.LogInformation("Successfully received review from model '{Model}'.", model);
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Propagate cancellation immediately — do not fall through to the next model.
                    throw;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Gemini model '{Model}' returned {StatusCode}. Trying fallback model in 1.5s...", model, ex.StatusCode);
                    await Task.Delay(1500, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Gemini model '{Model}' failed. Trying next fallback model...", model);
                }
            }

            if (!string.IsNullOrWhiteSpace(jsonResponse))
            {
                var (parsedComments, summary) = ParseGeminiResponse(jsonResponse, context);
                _logger.LogInformation("Chunk {Index}/{Total}: Extracted {Count} finding(s).", i + 1, chunks.Count, parsedComments.Count);
                comments.AddRange(parsedComments);

                if (!string.IsNullOrWhiteSpace(summary))
                {
                    summaries.Add(summary);
                }
            }
            else
            {
                _logger.LogError("Failed to get review response from any Gemini model for chunk {Index}.", i + 1);
                throw new HttpRequestException($"All Gemini models ({string.Join(", ", modelsToTry)}) failed to complete review for chunk {i + 1}. Please verify your API key and quota.");
            }
        }

        if (summaries.Count > 0)
        {
            Summary = string.Join("\n\n", summaries);
        }

        var sortedComments = comments
            .OrderByDescending(c => c.Severity) // High (Error) -> Medium (Warning) -> Low (Info)
            .ThenBy(c => c.FilePath)
            .ThenBy(c => c.Line)
            .ToList();

        _logger.LogInformation("Gemini AI review finished. Generated {Count} issue(s).", sortedComments.Count);
        return sortedComments;
    }

    public static List<string> BuildDiffChunks(PullRequestContext context, int maxChunkSize)
    {
        return BuildDiffChunks(context, maxChunkSize, options: null);
    }

    public static List<string> BuildDiffChunks(PullRequestContext context, int maxChunkSize, GeminiOptions? options)
    {
        var chunks = new List<string>();
        var currentChunk = new StringBuilder();
        var opts = options ?? new GeminiOptions();
        int filesInChunk = 0;

        foreach (var file in context.ChangedFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Patch) || file.Status.Equals("removed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var filePayload = BuildFileContext(file, opts) + "\n\n";

            bool exceedsChars = (currentChunk.Length + filePayload.Length > maxChunkSize && currentChunk.Length > 0);
            bool exceedsFiles = (filesInChunk >= opts.MaxFilesPerChunk && currentChunk.Length > 0);

            if (exceedsChars || exceedsFiles)
            {
                chunks.Add(currentChunk.ToString());
                currentChunk.Clear();
                filesInChunk = 0;
            }

            if (filePayload.Length > maxChunkSize && currentChunk.Length == 0)
            {
                chunks.Add(filePayload);
                continue;
            }

            currentChunk.Append(filePayload);
            filesInChunk++;
        }

        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString());
        }

        return chunks;
    }

    public static string BuildFileContext(PullRequestFileChange file, GeminiOptions? options = null)
    {
        var opts = options ?? new GeminiOptions();
        var addedLines = file.GetAddedOrModifiedLines();
        var addedLinesStr = addedLines.Count > 0 ? string.Join(", ", addedLines) : "None (deleted or non-code change)";

        var sb = new StringBuilder();
        sb.AppendLine($"--- File: {file.FileName} (Status: {file.Status}) ---");
        sb.AppendLine($"CHANGED / ADDED LINES TO REVIEW: [{addedLinesStr}]");

        if (string.IsNullOrWhiteSpace(file.FullContent))
        {
            // Diff-only fallback when full content is unavailable
            sb.AppendLine("GIT UNIFIED DIFF:");
            sb.AppendLine("```diff");
            sb.AppendLine($"--- a/{file.FileName}");
            sb.AppendLine($"+++ b/{file.FileName}");
            sb.AppendLine(file.Patch);
            sb.AppendLine("```");
            return sb.ToString();
        }

        var lines = file.FullContent.Split('\n');
        int totalLines = lines.Length;

        if (totalLines <= opts.MaxFullFileLines)
        {
            // Small/Medium file: Include full file context with 1-based line numbers
            sb.AppendLine($"FULL FILE CONTEXT ({totalLines} lines - READ-ONLY REFERENCE):");
            sb.AppendLine("```");
            for (int i = 0; i < totalLines; i++)
            {
                sb.AppendLine($"{i + 1,4} | {lines[i].TrimEnd('\r')}");
            }
            sb.AppendLine("```");
        }
        else
        {
            // Large file: Windowed context (imports/headers + surrounding hunk slices)
            sb.AppendLine($"WINDOWED FILE CONTEXT ({totalLines} lines - Large file, showing header & surrounding context):");
            sb.AppendLine("```");

            // Calculate intervals of lines to include (1-based)
            var intervals = new List<(int Start, int End)>();

            // Always include top header (first 30 lines or up to totalLines)
            int headerLines = Math.Min(30, totalLines);
            intervals.Add((1, headerLines));

            // Include surrounding context for each changed line
            foreach (var lineNum in addedLines)
            {
                int start = Math.Max(1, lineNum - opts.SurroundingContextLines);
                int end = Math.Min(totalLines, lineNum + opts.SurroundingContextLines);
                intervals.Add((start, end));
            }

            // Merge overlapping intervals
            var merged = new List<(int Start, int End)>();
            foreach (var interval in intervals.OrderBy(i => i.Start))
            {
                if (merged.Count == 0 || merged[^1].End < interval.Start - 1)
                {
                    merged.Add(interval);
                }
                else
                {
                    var last = merged[^1];
                    merged[^1] = (last.Start, Math.Max(last.End, interval.End));
                }
            }

            // Output lines with gap markers
            int previousEnd = 0;
            foreach (var (start, end) in merged)
            {
                if (previousEnd > 0 && start > previousEnd + 1)
                {
                    int omittedCount = start - previousEnd - 1;
                    sb.AppendLine($"     | // ... [{omittedCount} lines omitted (lines {previousEnd + 1} to {start - 1})] ...");
                }

                for (int i = start; i <= end; i++)
                {
                    sb.AppendLine($"{i,4} | {lines[i - 1].TrimEnd('\r')}");
                }

                previousEnd = end;
            }

            if (previousEnd < totalLines)
            {
                sb.AppendLine($"     | // ... [{totalLines - previousEnd} lines omitted to EOF] ...");
            }

            sb.AppendLine("```");
        }

        sb.AppendLine("GIT UNIFIED DIFF:");
        sb.AppendLine("```diff");
        sb.AppendLine($"--- a/{file.FileName}");
        sb.AppendLine($"+++ b/{file.FileName}");
        sb.AppendLine(file.Patch);
        sb.AppendLine("```");

        return sb.ToString();
    }

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public (List<ReviewComment> Comments, string? Summary) ParseGeminiResponse(string json, PullRequestContext? context = null)
    {
        var comments = new List<ReviewComment>();
        string? summary = null;

        var cleanJson = json.Trim();
        if (cleanJson.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            cleanJson = cleanJson[7..];
        }
        else if (cleanJson.StartsWith("```"))
        {
            cleanJson = cleanJson[3..];
        }

        if (cleanJson.EndsWith("```"))
        {
            cleanJson = cleanJson[..^3];
        }

        cleanJson = cleanJson.Trim();

        // Sanitize raw unescaped newlines/control characters inside JSON strings (RFC 8259 compliance)
        var sanitizedJson = SanitizeJson(cleanJson);

        try
        {
            using var doc = JsonDocument.Parse(sanitizedJson, DocumentOptions);
            var root = doc.RootElement;

            if (root.TryGetProperty("summary", out var summaryProp) && summaryProp.ValueKind == JsonValueKind.String)
            {
                summary = summaryProp.GetString();
            }

            if (root.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
            {
                foreach (var finding in findings.EnumerateArray())
                {
                    var file = finding.TryGetProperty("file", out var fp) ? fp.GetString() ?? "unknown" : "unknown";
                    var line = finding.TryGetProperty("line", out var lp) && lp.TryGetInt32(out var l) ? l : 1;
                    var message = finding.TryGetProperty("message", out var mp) ? mp.GetString() ?? "" : "";

                    var priorityStr = finding.TryGetProperty("priority", out var pp) ? pp.GetString() :
                                      finding.TryGetProperty("severity", out var sp) ? sp.GetString() : "medium";

                    var severity = (priorityStr?.ToLowerInvariant()) switch
                    {
                        "high" or "error" or "critical" => AnalyzerSeverity.Error,
                        "low" or "info" => AnalyzerSeverity.Info,
                        _ => AnalyzerSeverity.Warning
                    };

                    var priorityBadge = severity switch
                    {
                        AnalyzerSeverity.Error => "🔴 **[HIGH PRIORITY]**",
                        AnalyzerSeverity.Warning => "🟡 **[MEDIUM PRIORITY]**",
                        _ => "🟢 **[LOW PRIORITY]**"
                    };

                    // Resolve the reported file path against the actual changed files in the PR.
                    // Use a separate variable so the parsed value is never silently mutated.
                    string resolvedFile = file;
                    int? diffPosition = null;
                    if (context != null)
                    {
                        var matchingFile = context.ChangedFiles.FirstOrDefault(f =>
                            f.FileName.Equals(file, StringComparison.OrdinalIgnoreCase) ||
                            f.FileName.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith("/" + f.FileName, StringComparison.OrdinalIgnoreCase));

                        if (matchingFile != null)
                        {
                            resolvedFile = matchingFile.FileName;
                            diffPosition = FindDiffPosition(matchingFile.Patch, line);
                            if (!diffPosition.HasValue)
                            {
                                diffPosition = FindClosestDiffPosition(matchingFile.Patch, line);
                            }
                        }
                    }

                    comments.Add(new ReviewComment
                    {
                        FilePath = resolvedFile,
                        Line = line,
                        DiffPosition = diffPosition,
                        Body = $"🤖 **Gemini AI Review** {priorityBadge}\n\n{message}",
                        Severity = severity,
                        AnalyzerName = Name
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Standard JsonDocument.Parse failed on sanitized JSON. Attempting fallback regex parser...");
            return FallbackRegexParse(sanitizedJson, context);
        }

        return (comments, summary);
    }

    /// <summary>
    /// Fallback parser using regular expressions to extract findings and summary when JsonDocument fails on malformed JSON.
    /// </summary>
    private (List<ReviewComment> Comments, string? Summary) FallbackRegexParse(string json, PullRequestContext? context)
    {
        var comments = new List<ReviewComment>();
        string? summary = null;

        var summaryMatch = Regex.Match(json, @"""summary""\s*:\s*""(?<s>(?:\\.|[^""\\])*)""", RegexOptions.Singleline);
        if (summaryMatch.Success)
        {
            summary = Regex.Unescape(summaryMatch.Groups["s"].Value);
        }

        // Match individual finding objects in the findings array
        var findingRegex = new Regex(
            @"\{\s*""file""\s*:\s*""(?<file>[^""]+)""\s*,\s*""line""\s*:\s*(?<line>\d+)\s*,\s*""(?:priority|severity)""\s*:\s*""(?<priority>[^""]+)""\s*,\s*""message""\s*:\s*""(?<message>(?:\\.|[^""\\])*)""\s*\}",
            RegexOptions.Singleline);

        var matches = findingRegex.Matches(json);
        foreach (Match match in matches)
        {
            var file = match.Groups["file"].Value;
            var line = int.Parse(match.Groups["line"].Value);
            var priorityStr = match.Groups["priority"].Value;
            var message = Regex.Unescape(match.Groups["message"].Value);

            var severity = (priorityStr.ToLowerInvariant()) switch
            {
                "high" or "error" or "critical" => AnalyzerSeverity.Error,
                "low" or "info" => AnalyzerSeverity.Info,
                _ => AnalyzerSeverity.Warning
            };

            var priorityBadge = severity switch
            {
                AnalyzerSeverity.Error => "🔴 **[HIGH PRIORITY]**",
                AnalyzerSeverity.Warning => "🟡 **[MEDIUM PRIORITY]**",
                _ => "🟢 **[LOW PRIORITY]**"
            };

            string resolvedFile = file;
            int? diffPosition = null;
            if (context != null)
            {
                var matchingFile = context.ChangedFiles.FirstOrDefault(f =>
                    f.FileName.Equals(file, StringComparison.OrdinalIgnoreCase) ||
                    f.FileName.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith("/" + f.FileName, StringComparison.OrdinalIgnoreCase));

                if (matchingFile != null)
                {
                    resolvedFile = matchingFile.FileName;
                    diffPosition = FindDiffPosition(matchingFile.Patch, line);
                    if (!diffPosition.HasValue)
                    {
                        diffPosition = FindClosestDiffPosition(matchingFile.Patch, line);
                    }
                }
            }

            comments.Add(new ReviewComment
            {
                FilePath = resolvedFile,
                Line = line,
                DiffPosition = diffPosition,
                Body = $"🤖 **Gemini AI Review** {priorityBadge}\n\n{message}",
                Severity = severity,
                AnalyzerName = Name
            });
        }

        return (comments, summary);
    }

    /// <summary>
    /// Sanitizes raw JSON from LLM responses by escaping raw unescaped control characters (such as 0x0A / \n)
    /// inside JSON string literals that violate RFC 8259.
    /// </summary>
    public static string SanitizeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;

        var sb = new StringBuilder(json.Length + 128);
        bool inString = false;
        bool isEscaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (inString)
            {
                if (c == '\\')
                {
                    isEscaped = !isEscaped;
                    sb.Append(c);
                }
                else if (c == '"' && !isEscaped)
                {
                    inString = false;
                    sb.Append(c);
                }
                else
                {
                    if (c == '\n')
                    {
                        sb.Append("\\n");
                    }
                    else if (c == '\r')
                    {
                        sb.Append("\\r");
                    }
                    else if (c == '\t')
                    {
                        sb.Append("\\t");
                    }
                    else if (c < 0x20)
                    {
                        sb.Append($"\\u{(int)c:x4}");
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    isEscaped = false;
                }
            }
            else
            {
                if (c == '"')
                {
                    inString = true;
                    isEscaped = false;
                }
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public static int? FindDiffPosition(string? patch, int targetLine)
    {
        if (string.IsNullOrWhiteSpace(patch)) return null;

        var lines = patch.Split('\n');
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
                if (currentLine == targetLine)
                {
                    return diffPosition;
                }
                currentLine++;
            }
            else if (line.StartsWith('-') && !line.StartsWith("---"))
            {
                // Deleted line in diff
            }
            else if (line.StartsWith(' '))
            {
                if (currentLine == targetLine)
                {
                    return diffPosition;
                }
                currentLine++;
            }
        }

        return null;
    }

    /// <summary>
    /// When a reported line does not fall exactly on a modified line in the patch hunk,
    /// finds the closest available modified/context line position in the patch so the inline comment
    /// is placed as close to the issue as possible rather than being dropped.
    /// </summary>
    public static int? FindClosestDiffPosition(string? patch, int targetLine)
    {
        if (string.IsNullOrWhiteSpace(patch)) return null;

        var lines = patch.Split('\n');
        int currentLine = 0;
        int diffPosition = 0;
        bool foundFirstHunk = false;

        int? closestDiffPos = null;
        int minDistance = int.MaxValue;

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
                int distance = Math.Abs(currentLine - targetLine);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    closestDiffPos = diffPosition;
                }
                currentLine++;
            }
            else if (line.StartsWith(' '))
            {
                int distance = Math.Abs(currentLine - targetLine);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    closestDiffPos = diffPosition;
                }
                currentLine++;
            }
        }

        return closestDiffPos;
    }
}
