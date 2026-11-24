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

            var userPrompt = CodeReviewPrompt.BuildUserMessage(context.Title, context.Author, context.Description, chunk, context.PriorDefects);
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
                var (parsedComments, summary) = ParseGeminiResponse(jsonResponse, context, currentWorkingModel);
                _logger.LogInformation("Chunk {Index}/{Total}: Extracted {Count} finding(s) using model '{Model}'.", i + 1, chunks.Count, parsedComments.Count, currentWorkingModel);
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

        var sortedComments = DeduplicateComments(comments);

        _logger.LogInformation("Gemini AI review finished. Generated {Count} issue(s) after deduplication.", sortedComments.Count);
        return sortedComments;
    }

    /// <summary>
    /// Deduplicates review comments targeting the same line of code in the same file.
    /// When multiple findings target the same line, retains the one with highest severity (Error > Warning > Info).
    /// Ties are broken by highest category rank (Security > Reliability > Input Validation > Performance > Maintainability).
    /// </summary>
    public static List<ReviewComment> DeduplicateComments(IEnumerable<ReviewComment> comments)
    {
        return comments
            .GroupBy(c => (FilePath: c.FilePath.ToLowerInvariant(), c.Line))
            .Select(group => group
                .OrderByDescending(c => c.Severity) // High (Error) -> Medium (Warning) -> Low (Info)
                .ThenBy(c => GetCategoryRank(c.Category)) // Security (1) > Reliability (2) > Input Validation (3) > Performance (4) > Maintainability (5)
                .First())
            .OrderByDescending(c => c.Severity)
            .ThenBy(c => c.FilePath)
            .ThenBy(c => c.StartLine ?? c.Line)
            .ThenBy(c => c.Line)
            .ToList();
    }

    /// <summary>
    /// Returns numerical priority rank for canonical review categories (lower = higher priority).
    /// Ensures Security issues always take precedence over Reliability, which takes precedence over Maintainability.
    /// </summary>
    public static int GetCategoryRank(string? category) => (category?.ToLowerInvariant()) switch
    {
        "security" => 1,
        "reliability" => 2,
        "input validation" => 3,
        "performance" or "concurrency" => 4,
        "maintainability" or "error handling" => 5,
        _ => 6
    };

    public static List<string> BuildDiffChunks(PullRequestContext context, int maxChunkSize)
    {
        return BuildDiffChunks(context, maxChunkSize, options: null);
    }

    public static List<string> BuildDiffChunks(PullRequestContext context, int maxChunkSize, GeminiOptions? options)
    {
        var opts = options ?? new GeminiOptions();
        var eligibleFiles = context.ChangedFiles
            .Where(f => !string.IsNullOrWhiteSpace(f.Patch) && !f.Status.Equals("removed", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (eligibleFiles.Count == 0) return [];

        var chunks = new List<string>();
        var currentBatch = new List<PullRequestFileChange>();
        int currentChars = 0;

        foreach (var file in eligibleFiles)
        {
            var filePayload = BuildFileContext(file, opts);
            bool exceedsChars = (currentChars + filePayload.Length > maxChunkSize && currentBatch.Count > 0);
            bool exceedsFiles = (currentBatch.Count >= opts.MaxFilesPerChunk && currentBatch.Count > 0);

            if (exceedsChars || exceedsFiles)
            {
                chunks.Add(RenderBatchPayload(currentBatch, opts));
                currentBatch.Clear();
                currentChars = 0;
            }

            currentBatch.Add(file);
            currentChars += filePayload.Length;
        }

        if (currentBatch.Count > 0)
        {
            chunks.Add(RenderBatchPayload(currentBatch, opts));
        }

        return chunks;
    }

    /// <summary>
    /// Renders a unified chunk payload containing one or more changed files.
    /// When multiple files are present, prepends an explicit Batch File Manifest so the LLM
    /// is explicitly instructed on how many files exist and does not stop after the first file.
    /// </summary>
    public static string RenderBatchPayload(List<PullRequestFileChange> batch, GeminiOptions opts)
    {
        var sb = new StringBuilder();

        if (batch.Count > 1)
        {
            sb.AppendLine("================================================================================");
            sb.AppendLine($"FILES TO AUDIT IN THIS CHUNK (Total: {batch.Count} files):");
            for (int i = 0; i < batch.Count; i++)
            {
                var f = batch[i];
                var lines = f.GetAddedOrModifiedLines();
                var linesStr = lines.Count > 0 ? string.Join(", ", lines) : "Non-code / metadata change";
                sb.AppendLine($"  [{i + 1}/{batch.Count}] {f.FileName} (Changed lines: [{linesStr}])");
            }
            sb.AppendLine();
            sb.AppendLine("CRITICAL MULTI-FILE INSTRUCTION:");
            sb.AppendLine($"You MUST systematically audit ALL {batch.Count} files listed above from [1/{batch.Count}] to [{batch.Count}/{batch.Count}].");
            sb.AppendLine("DO NOT stop after inspecting file [1]. You are required to review every file and return findings across all files.");
            sb.AppendLine("================================================================================\n");
        }

        for (int i = 0; i < batch.Count; i++)
        {
            var f = batch[i];
            sb.AppendLine(BuildFileContext(f, opts, fileIndex: i + 1, totalFilesInBatch: batch.Count));
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    public static string BuildFileContext(PullRequestFileChange file, GeminiOptions? options = null, int fileIndex = 1, int totalFilesInBatch = 1)
    {
        var opts = options ?? new GeminiOptions();
        var addedLines = file.GetAddedOrModifiedLines();
        var addedLinesStr = addedLines.Count > 0 ? string.Join(", ", addedLines) : "None (deleted or non-code change)";

        var sb = new StringBuilder();
        var header = totalFilesInBatch > 1
            ? $">>> [FILE {fileIndex}/{totalFilesInBatch}] {file.FileName} (Status: {file.Status})"
            : $"--- File: {file.FileName} (Status: {file.Status}) ---";

        sb.AppendLine("================================================================================");
        sb.AppendLine(header);
        sb.AppendLine($"CHANGED / ADDED LINES TO REVIEW: [{addedLinesStr}]");
        sb.AppendLine("================================================================================");

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

    public (List<ReviewComment> Comments, string? Summary) ParseGeminiResponse(string json, PullRequestContext? context = null, string? modelName = null)
    {
        var comments = new List<ReviewComment>();
        string? summary = null;
        var analyzerLabel = !string.IsNullOrWhiteSpace(modelName) ? $"Gemini AI ({modelName})" : Name;

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

            // 0. Process defect reconciliations (verifying whether prior reported defects are resolved or still active)
            if (root.TryGetProperty("defect_reconciliations", out var reconciliations) &&
                reconciliations.ValueKind == JsonValueKind.Array &&
                context != null &&
                context.PriorDefects.Count > 0)
            {
                foreach (var item in reconciliations.EnumerateArray())
                {
                    long commentId = 0;
                    if (item.TryGetProperty("comment_id", out var idProp))
                    {
                        if (idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt64(out var idNum))
                        {
                            commentId = idNum;
                        }
                        else if (idProp.ValueKind == JsonValueKind.String && long.TryParse(idProp.GetString(), out var parsedId))
                        {
                            commentId = parsedId;
                        }
                    }

                    var status = item.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "" : "";
                    var note = item.TryGetProperty("resolution_note", out var noteProp) ? noteProp.GetString() : null;

                    var priorDefect = context.PriorDefects.FirstOrDefault(d => d.CommentId == commentId);
                    if (priorDefect == null && item.TryGetProperty("file", out var fileProp))
                    {
                        var fName = fileProp.GetString();
                        int? fLine = item.TryGetProperty("line", out var lineProp) && lineProp.TryGetInt32(out var lVal) ? lVal : null;
                        if (!string.IsNullOrWhiteSpace(fName))
                        {
                            priorDefect = context.PriorDefects.FirstOrDefault(d =>
                                d.FilePath.Equals(fName, StringComparison.OrdinalIgnoreCase) &&
                                (!fLine.HasValue || Math.Abs(d.Line - fLine.Value) <= 10));
                        }
                    }

                    if (priorDefect != null)
                    {
                        priorDefect.ReconciliationStatus = status;
                        if (status.Equals("resolved", StringComparison.OrdinalIgnoreCase))
                        {
                            priorDefect.IsResolved = true;
                            priorDefect.ResolutionExplanation = note;
                            _logger.LogInformation("Prior defect #{CommentId} on {File}:{Line} verified by AI as RESOLVED: {Note}",
                                commentId, priorDefect.FilePath, priorDefect.Line, note);
                        }
                        else
                        {
                            priorDefect.IsResolved = false;
                            _logger.LogInformation("Prior defect #{CommentId} on {File}:{Line} verified by AI as STILL ACTIVE: {Note}",
                                commentId, priorDefect.FilePath, priorDefect.Line, note);
                        }
                    }
                }
            }

            // 1. Process per-file reviews schema (Approach 2: guarantees multi-file completeness in single API call)
            if (root.TryGetProperty("file_reviews", out var fileReviews) && fileReviews.ValueKind == JsonValueKind.Array)
            {
                foreach (var fileReview in fileReviews.EnumerateArray())
                {
                    var file = fileReview.TryGetProperty("file", out var fp) ? fp.GetString() ?? "unknown" : "unknown";
                    if (fileReview.TryGetProperty("findings", out var fileFindings) && fileFindings.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var finding in fileFindings.EnumerateArray())
                        {
                            var comment = ParseFindingElement(finding, file, context, analyzerLabel);
                            if (comment != null) comments.Add(comment);
                        }
                    }
                }
            }

            // 2. Also process root-level findings if present (flat schema backwards compatibility / fallback)
            if (root.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
            {
                foreach (var finding in findings.EnumerateArray())
                {
                    var file = finding.TryGetProperty("file", out var fp) ? fp.GetString() ?? "unknown" : "unknown";
                    var comment = ParseFindingElement(finding, file, context, analyzerLabel);
                    if (comment != null) comments.Add(comment);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Standard JsonDocument.Parse failed on sanitized JSON. Attempting fallback regex parser...");
            return FallbackRegexParse(sanitizedJson, context, modelName);
        }

        return (comments, summary);
    }

    private static ReviewComment? ParseFindingElement(JsonElement finding, string fallbackFile, PullRequestContext? context, string analyzerLabel)
    {
        var file = finding.TryGetProperty("file", out var fp) ? fp.GetString() ?? fallbackFile : fallbackFile;
        var line = finding.TryGetProperty("line", out var lp) && lp.TryGetInt32(out var l) ? l : 1;
        int? startLine = null;
        if (finding.TryGetProperty("start_line", out var slp) && slp.TryGetInt32(out var sl) && sl > 0 && sl < line)
        {
            startLine = sl;
        }
        var message = finding.TryGetProperty("message", out var mp) ? mp.GetString() ?? "" : "";

        var priorityStr = finding.TryGetProperty("priority", out var pp) ? pp.GetString() :
                          finding.TryGetProperty("severity", out var sp) ? sp.GetString() : "medium";

        var severity = (priorityStr?.ToLowerInvariant()) switch
        {
            "critical" => AnalyzerSeverity.Critical,
            "high" or "error" => AnalyzerSeverity.Error,
            "low" or "info" => AnalyzerSeverity.Info,
            _ => AnalyzerSeverity.Warning
        };

        var title = finding.TryGetProperty("title", out var tp) ? tp.GetString() : null;
        var category = finding.TryGetProperty("category", out var cp) ? cp.GetString() : null;
        var whyItMatters = finding.TryGetProperty("why_it_matters", out var wp) ? wp.GetString() : null;
        string? suggestedFix = null;
        if (finding.TryGetProperty("suggested_fix", out var sfp) && sfp.ValueKind == JsonValueKind.String)
        {
            suggestedFix = sfp.GetString();
        }

        string resolvedFile = file;
        int resolvedLine = line;
        int? diffPosition = null;
        bool isNativeSuggestion = true;

        if (context != null)
        {
            var matchingFile = context.ChangedFiles.FirstOrDefault(f =>
                f.FileName.Equals(file, StringComparison.OrdinalIgnoreCase) ||
                f.FileName.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith("/" + f.FileName, StringComparison.OrdinalIgnoreCase));

            if (matchingFile != null)
            {
                resolvedFile = matchingFile.FileName;
                resolvedLine = AdjustMeaningfulLine(matchingFile, line);
                if (startLine.HasValue)
                {
                    var resolvedStart = AdjustMeaningfulLine(matchingFile, startLine.Value);
                    if (resolvedStart < resolvedLine)
                    {
                        startLine = resolvedStart;
                    }
                    else
                    {
                        startLine = null; // invalid range after adjustment, fall back to single line
                    }
                }

                diffPosition = FindDiffPosition(matchingFile.Patch, resolvedLine);
                if (!diffPosition.HasValue)
                {
                    diffPosition = FindClosestDiffPosition(matchingFile.Patch, resolvedLine);
                }

                // Check target line content to preserve indentation and guard solitary braces
                int baseIndentLine = startLine ?? resolvedLine;
                var targetLineContent = GetLineContent(matchingFile, baseIndentLine);
                if (targetLineContent != null)
                {
                    var trimmed = targetLineContent.Trim();
                    if (IsMeaninglessBraceOrEmpty(trimmed))
                    {
                        // Target line is a closing brace/empty. Replacing a brace with code breaks syntax in GitHub suggestions!
                        isNativeSuggestion = false;
                    }
                    else if (!string.IsNullOrWhiteSpace(suggestedFix))
                    {
                        // Guard against no-op suggestions: if suggestedFix is identical to existing line/range, discard it!
                        string? targetTextToCompare = null;
                        if (startLine.HasValue)
                        {
                            var rangeLines = new List<string>();
                            for (int lineIdx = startLine.Value; lineIdx <= resolvedLine; lineIdx++)
                            {
                                var lineText = GetLineContent(matchingFile, lineIdx);
                                if (lineText != null) rangeLines.Add(lineText);
                            }
                            if (rangeLines.Count > 0)
                            {
                                targetTextToCompare = string.Join("\n", rangeLines);
                            }
                        }
                        else
                        {
                            targetTextToCompare = targetLineContent;
                        }

                        if (targetTextToCompare != null && string.Equals(
                            suggestedFix.Trim().Replace("\r\n", "\n"),
                            targetTextToCompare.Trim().Replace("\r\n", "\n"),
                            StringComparison.Ordinal))
                        {
                            suggestedFix = null;
                        }
                        else
                        {
                            // Preserve leading indentation of the line being replaced so GitHub diff aligns seamlessly
                            int indentLen = targetLineContent.TakeWhile(char.IsWhiteSpace).Count();
                            if (indentLen > 0)
                            {
                                var indent = targetLineContent[..indentLen];
                                if (!suggestedFix.StartsWith(indent) && !suggestedFix.StartsWith(" ") && !suggestedFix.StartsWith("\t"))
                                {
                                    var fixLines = suggestedFix.Split('\n');
                                    suggestedFix = string.Join("\n", fixLines.Select(l => string.IsNullOrWhiteSpace(l) ? l : indent + l));
                                }
                            }
                        }
                    }
                }
            }
        }

        var body = BuildRichCommentBody(severity, title, category, message, whyItMatters, suggestedFix, isNativeSuggestion);

        return new ReviewComment
        {
            FilePath = resolvedFile,
            Line = resolvedLine,
            StartLine = startLine,
            DiffPosition = diffPosition,
            Body = body,
            Severity = severity,
            AnalyzerName = analyzerLabel,
            Title = title,
            Category = category,
            WhyItMatters = whyItMatters,
            SuggestedFix = suggestedFix
        };
    }

    /// <summary>
    /// Builds a rich, structured GitHub-flavored markdown body for an inline review comment.
    /// Renders with severity icon, title, category badge, problem description, impact explanation,
    /// and a native GitHub suggestion block for one-click apply.
    /// </summary>
    public static string BuildRichCommentBody(
        AnalyzerSeverity severity,
        string? title,
        string? category,
        string message,
        string? whyItMatters,
        string? suggestedFix,
        bool isNativeSuggestion = true)
    {
        var sb = new StringBuilder();

        // Header line: severity icon + label + title
        var (severityLabel, severityIcon) = severity switch
        {
            AnalyzerSeverity.Critical => ("CRITICAL", "🚨"),
            AnalyzerSeverity.Error => ("HIGH", "🔴"),
            AnalyzerSeverity.Warning => ("MEDIUM", "⚠️"),
            _ => ("LOW", "💡")
        };

        var displayTitle = !string.IsNullOrWhiteSpace(title) ? title : "Issue Identified";
        sb.AppendLine($"{severityIcon} **[{severityLabel}]** {displayTitle}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(category))
        {
            sb.AppendLine($"> Category: `{category}`");
            sb.AppendLine();
        }

        // Collapsible Inspection Details block matching Quality Gate format
        sb.AppendLine("<details open>");
        sb.AppendLine("<summary>Inspection Details</summary>");
        sb.AppendLine();
        sb.AppendLine("**Problem:**");
        sb.AppendLine(message);
        sb.AppendLine();

        // Risk & Impact / Why It Matters
        if (!string.IsNullOrWhiteSpace(whyItMatters))
        {
            sb.AppendLine("**Why It Matters:**");
            sb.AppendLine(whyItMatters);
            sb.AppendLine();
        }

        // Recommended Solution / Suggested Fix
        if (!string.IsNullOrWhiteSpace(suggestedFix))
        {
            sb.AppendLine("**Suggested Fix:**");
            if (isNativeSuggestion)
            {
                sb.AppendLine("```suggestion");
                sb.AppendLine(suggestedFix);
                sb.AppendLine("```");
            }
            else
            {
                sb.AppendLine("```");
                sb.AppendLine(suggestedFix);
                sb.AppendLine("```");
            }
            sb.AppendLine();
        }

        sb.AppendLine("</details>");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Fallback parser using regular expressions to extract findings and summary when JsonDocument fails on malformed JSON.
    /// </summary>
    private (List<ReviewComment> Comments, string? Summary) FallbackRegexParse(string json, PullRequestContext? context, string? modelName = null)
    {
        var comments = new List<ReviewComment>();
        string? summary = null;
        var analyzerLabel = !string.IsNullOrWhiteSpace(modelName) ? $"Gemini AI ({modelName})" : Name;

        var summaryMatch = Regex.Match(json, @"""summary""\s*:\s*""(?<s>(?:\\.|[^""\\])*)""", RegexOptions.Singleline);
        if (summaryMatch.Success)
        {
            summary = Regex.Unescape(summaryMatch.Groups["s"].Value);
        }

        // Match individual finding objects in the findings array
        var findingRegex = new Regex(
            @"\{\s*""file""\s*:\s*""(?<file>[^""]+)""\s*,\s*""line""\s*:\s*(?<line>\d+)(?:\s*,\s*""start_line""\s*:\s*(?<start_line>\d+))?\s*,\s*""(?:priority|severity)""\s*:\s*""(?<priority>[^""]+)""\s*,\s*""message""\s*:\s*""(?<message>(?:\\.|[^""\\])*)""\s*\}",
            RegexOptions.Singleline);

        var matches = findingRegex.Matches(json);
        foreach (Match match in matches)
        {
            var file = match.Groups["file"].Value;
            var line = int.Parse(match.Groups["line"].Value);
            int? startLine = null;
            if (match.Groups["start_line"].Success && int.TryParse(match.Groups["start_line"].Value, out var sl) && sl > 0 && sl < line)
            {
                startLine = sl;
            }
            var priorityStr = match.Groups["priority"].Value;
            var message = Regex.Unescape(match.Groups["message"].Value);

            var severity = (priorityStr.ToLowerInvariant()) switch
            {
                "critical" => AnalyzerSeverity.Critical,
                "high" or "error" => AnalyzerSeverity.Error,
                "low" or "info" => AnalyzerSeverity.Info,
                _ => AnalyzerSeverity.Warning
            };

            string resolvedFile = file;
            int resolvedLine = line;
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
                    resolvedLine = AdjustMeaningfulLine(matchingFile, line);
                    if (startLine.HasValue)
                    {
                        var resolvedStart = AdjustMeaningfulLine(matchingFile, startLine.Value);
                        if (resolvedStart < resolvedLine)
                        {
                            startLine = resolvedStart;
                        }
                        else
                        {
                            startLine = null;
                        }
                    }

                    diffPosition = FindDiffPosition(matchingFile.Patch, resolvedLine);
                    if (!diffPosition.HasValue)
                    {
                        diffPosition = FindClosestDiffPosition(matchingFile.Patch, resolvedLine);
                    }
                }
            }

            // Fallback parser doesn't have structured fields — build a simpler rich body
            var body = BuildRichCommentBody(severity, title: null, category: null, message, whyItMatters: null, suggestedFix: null);

            comments.Add(new ReviewComment
            {
                FilePath = resolvedFile,
                Line = resolvedLine,
                StartLine = startLine,
                DiffPosition = diffPosition,
                Body = body,
                Severity = severity,
                AnalyzerName = analyzerLabel
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
                currentLine = int.Parse(match.Groups[1].Value);
                if (!foundFirstHunk)
                {
                    foundFirstHunk = true;
                }
                else
                {
                    diffPosition++;
                }
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
                // Deleted line in diff (counts as a position in GitHub diff, but does not advance currentLine in new file)
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
                currentLine = int.Parse(match.Groups[1].Value);
                if (!foundFirstHunk)
                {
                    foundFirstHunk = true;
                }
                else
                {
                    diffPosition++;
                }
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
            else if (line.StartsWith('-') && !line.StartsWith("---"))
            {
                // Deleted line in diff
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

    /// <summary>
    /// Checks if targetLine points to a meaningless line (such as a solitary opening or closing brace '{' or '}',
    /// or empty whitespace). If so, scans upward to find the closest meaningful code statement (e.g. method signature,
    /// statement, condition) so the GitHub inline review comment displays the actual code in context.
    /// </summary>
    public static int AdjustMeaningfulLine(PullRequestFileChange? file, int targetLine)
    {
        if (file == null || targetLine <= 0) return targetLine;

        // Attempt 1: Check in FullContent if available
        if (!string.IsNullOrWhiteSpace(file.FullContent))
        {
            var lines = file.FullContent.Split('\n');
            if (targetLine <= lines.Length)
            {
                var targetText = lines[targetLine - 1].Trim();
                if (IsMeaninglessBraceOrEmpty(targetText))
                {
                    // Scan upward to find the nearest meaningful line
                    for (int i = targetLine - 1; i >= 1; i--)
                    {
                        var candidate = lines[i - 1].Trim();
                        if (!IsMeaninglessBraceOrEmpty(candidate))
                        {
                            return i;
                        }
                    }
                }
            }
            return targetLine;
        }

        // Attempt 2: If only Patch is available, scan patch lines
        if (!string.IsNullOrWhiteSpace(file.Patch))
        {
            var patchLines = file.Patch.Split('\n');
            int currentLine = 0;
            bool foundFirstHunk = false;
            var lineMap = new Dictionary<int, string>();

            foreach (var rawLine in patchLines)
            {
                var line = rawLine.TrimEnd('\r');
                var match = HunkRegex.Match(line);
                if (match.Success)
                {
                    foundFirstHunk = true;
                    currentLine = int.Parse(match.Groups[1].Value);
                    continue;
                }

                if (!foundFirstHunk) continue;

                if (line.StartsWith('+') && !line.StartsWith("+++"))
                {
                    lineMap[currentLine] = line[1..].Trim();
                    currentLine++;
                }
                else if (line.StartsWith(' '))
                {
                    lineMap[currentLine] = line[1..].Trim();
                    currentLine++;
                }
            }

            if (lineMap.TryGetValue(targetLine, out var text) && IsMeaninglessBraceOrEmpty(text))
            {
                // Scan upward for previous meaningful line in map
                for (int candidate = targetLine - 1; candidate >= 1; candidate--)
                {
                    if (lineMap.TryGetValue(candidate, out var prevText) && !IsMeaninglessBraceOrEmpty(prevText))
                    {
                        return candidate;
                    }
                }
            }
        }

        return targetLine;
    }

    /// <summary>
    /// Retrieves the text content of a specific 1-based line number in a changed file
    /// (using FullContent if available, or reconstructing from the git patch hunk lines).
    /// </summary>
    public static string? GetLineContent(PullRequestFileChange? file, int targetLine)
    {
        if (file == null || targetLine <= 0) return null;

        if (!string.IsNullOrWhiteSpace(file.FullContent))
        {
            var lines = file.FullContent.Split('\n');
            if (targetLine <= lines.Length)
            {
                return lines[targetLine - 1].TrimEnd('\r');
            }
        }

        if (!string.IsNullOrWhiteSpace(file.Patch))
        {
            var patchLines = file.Patch.Split('\n');
            int currentLine = 0;
            bool foundFirstHunk = false;

            foreach (var rawLine in patchLines)
            {
                var line = rawLine.TrimEnd('\r');
                var match = HunkRegex.Match(line);
                if (match.Success)
                {
                    foundFirstHunk = true;
                    currentLine = int.Parse(match.Groups[1].Value);
                    continue;
                }

                if (!foundFirstHunk) continue;

                if (line.StartsWith('+') && !line.StartsWith("+++"))
                {
                    if (currentLine == targetLine) return line[1..];
                    currentLine++;
                }
                else if (line.StartsWith(' '))
                {
                    if (currentLine == targetLine) return line[1..];
                    currentLine++;
                }
            }
        }

        return null;
    }

    public static bool IsMeaninglessBraceOrEmpty(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        return text is "{" or "}" or "};" or "});" or "()" or ") {" or "){" or "];" or "]" or ");" or ")";
    }
}
