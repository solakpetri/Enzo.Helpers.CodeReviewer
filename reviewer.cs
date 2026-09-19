#nullable enable

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

return await ReviewerApp.RunAsync(args);

static class ReviewerApp
{
    private const string OpenAiEndpoint = "https://api.openai.com/v1/responses";
    private const string GitHubApiRoot = "https://api.github.com";
    private const string GitHubUserAgent = "Enzo.Helpers.CodeReviewer";
    private const string Model = "gpt-4.1-mini";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--self-test")
            {
                return RunSelfTests();
            }

            var options = GetOptions(args);
            var diffPath = GetDiffPath(options.DiffPath);
            var diff = await File.ReadAllTextAsync(diffPath);
            var diffInfo = ParseUnifiedDiff(diff);
            var skills = await LoadSkillsAsync(options.SkillsDirectory);
            var apiKey = GetApiKey();
            var prompt = BuildReviewPrompt(skills, diff);
            var reviewJson = await RequestReviewJsonAsync(apiKey, prompt);
            var review = ParseReviewResult(reviewJson);

            ValidateReviewResult(review);
            var validation = ValidateFindingsAgainstDiff(review, diffInfo.ChangedLines);
            var report = BuildReviewReport(validation.Review, diffInfo.AddedLineCount);
            PrintReview(report);
            await PublishGitHubReviewAsync(report, validation.InlineFindings);
            return 0;
        }
        catch (ReviewFailureException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"OpenAI request failed: {ex.Message}");
            return 1;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Malformed model response: {ex.Message}");
            return 1;
        }
    }

    private static ReviewOptions GetOptions(string[] args)
    {
        const string usage = "Usage: dotnet reviewer.cs <diff-file> --skills <skills-directory>";

        if (args.Length == 0)
        {
            throw new ReviewFailureException($"Missing diff file argument.\n{usage}");
        }

        if (args.Length == 1)
        {
            throw new ReviewFailureException($"Missing required --skills <skills-directory>.\n{usage}");
        }

        if (args.Length == 2 && args[1] == "--skills")
        {
            throw new ReviewFailureException($"Missing skills directory after --skills.\n{usage}");
        }

        if (args.Length != 3 || args[1] != "--skills")
        {
            throw new ReviewFailureException($"Invalid arguments.\n{usage}");
        }

        return new ReviewOptions(args[0], Path.GetFullPath(args[2]));
    }

    private static string GetDiffPath(string diffFile)
    {
        var diffPath = Path.GetFullPath(diffFile);
        if (!File.Exists(diffPath))
        {
            throw new ReviewFailureException($"Diff file not found: {diffPath}");
        }

        return diffPath;
    }

    private static async Task<List<ReviewSkill>> LoadSkillsAsync(string skillsDirectory)
    {
        if (!Directory.Exists(skillsDirectory))
        {
            throw new ReviewFailureException($"Skills directory not found: {skillsDirectory}");
        }

        var skillFiles = Directory
            .EnumerateFiles(skillsDirectory, "SKILL.md", SearchOption.AllDirectories)
            .OrderBy(skillFile => Path.GetRelativePath(skillsDirectory, skillFile), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (skillFiles.Length == 0)
        {
            throw new ReviewFailureException($"No SKILL.md files found in: {skillsDirectory}");
        }

        var skills = new List<ReviewSkill>();
        foreach (var skillFile in skillFiles)
        {
            var content = await File.ReadAllTextAsync(skillFile);
            var skillName = Path.GetRelativePath(skillsDirectory, skillFile).Replace('\\', '/');
            skills.Add(new ReviewSkill(skillName, content.Trim()));
        }

        return skills;
    }

    private static string GetApiKey()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ReviewFailureException("OPENAI_API_KEY is not set.");
        }

        return apiKey;
    }

    private static string BuildReviewPrompt(IReadOnlyCollection<ReviewSkill> skills, string diff)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are an AI-assisted code reviewer for .NET changes.");
        builder.AppendLine();
        builder.AppendLine("Review only the supplied PR diff. Findings must refer to changed lines from the diff and must use the new/right-side line number whenever possible.");
        builder.AppendLine("Return findings and advice separately. Findings are actionable issues worth fixing. Advice is useful non-blocking guidance specific to the changed code.");
        builder.AppendLine("Return only concrete, actionable problems introduced or exposed by the pull request as findings. If there are no actionable issues, return an empty findings array.");
        builder.AppendLine("Prioritize correctness bugs, security vulnerabilities, concurrency problems, race conditions, async misuse, resource leaks, EF Core misuse, database consistency issues, broken error handling, meaningful architectural violations, important missing validation, significant performance problems, and missing tests where changed behavior creates meaningful regression risk.");
        builder.AppendLine("Do not report praise, positive observations, code summaries, things the code does correctly, compliments, stylistic preferences, formatting, naming trivia, subjective refactoring preferences, optional improvements without a concrete benefit, unchanged code, speculative problems without a plausible failure mode, or comments merely to demonstrate inspection.");
        builder.AppendLine("Every finding must represent something the developer should reasonably consider fixing. Do not manufacture findings so the review has content. Do not convert optional advice into low-severity findings.");
        builder.AppendLine("Every finding must include an action and a directly usable coding-agent prompt. The prompt must identify the file and problem, describe the expected correction, ask to preserve unrelated behavior, request focused validation or tests where appropriate, and avoid unrelated refactoring. Do not include credentials, secrets, or unnecessary repository information.");
        builder.AppendLine("Advice may discuss better framework APIs, .NET 10 features, modern replacements, maintainability improvements, meaningful performance improvements, or useful framework capabilities. Advice must be concrete, relevant to the changed code, and omitted when there is no useful PR-specific recommendation.");
        builder.AppendLine("External review skills may help identify problems, but these issue-only instructions are authoritative.");
        builder.AppendLine();
        builder.AppendLine("Severity must be exactly one of: high, medium, low.");
        builder.AppendLine("File must be a path from the supplied diff. Line must be a changed new/right-side line from that file. Do not invent locations outside the diff.");
        builder.AppendLine();
        builder.AppendLine("Review skills:");

        foreach (var skill in skills)
        {
            builder.AppendLine($"\n--- {skill.Name} ---");
            builder.AppendLine(skill.Content);
        }

        builder.AppendLine("\nDiff to review:");
        builder.AppendLine("```diff");
        builder.AppendLine(diff);
        builder.AppendLine("```");
        return builder.ToString();
    }

    private static async Task<string> RequestReviewJsonAsync(string apiKey, string prompt)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiEndpoint)
        {
            Content = new StringContent(CreateOpenAiRequestJson(prompt), Encoding.UTF8, "application/json")
        };

        using var response = await httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new ReviewFailureException($"OpenAI request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {GetOpenAiErrorMessage(responseBody, apiKey)}");
        }

        return ExtractResponseText(responseBody);
    }

    private static string CreateOpenAiRequestJson(string prompt) => $$"""
        {
          "model": "{{Model}}",
          "input": [
            {
              "role": "user",
              "content": {{JsonString(prompt)}}
            }
          ],
          "text": {
            "format": {
              "type": "json_schema",
              "name": "code_review_result",
              "strict": true,
              "schema": {
                "type": "object",
                "additionalProperties": false,
                  "required": ["findings", "advice"],
                  "properties": {
                    "findings": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["file", "line", "severity", "title", "message", "action", "agent_prompt"],
                        "properties": {
                          "file": { "type": "string" },
                          "line": { "type": "integer", "minimum": 1 },
                          "severity": { "type": "string", "enum": ["high", "medium", "low"] },
                          "title": { "type": "string" },
                          "message": { "type": "string" },
                          "action": { "type": "string" },
                          "agent_prompt": { "type": "string" }
                        }
                      }
                    },
                    "advice": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["message"],
                        "properties": {
                          "message": { "type": "string" }
                        }
                      }
                    }
                }
              }
            }
          }
        }
        """;

    private static string JsonString(string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStringValue(value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ExtractResponseText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);

        if (document.RootElement.TryGetProperty("output_text", out var outputText)
            && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString() ?? string.Empty;
        }

        if (!document.RootElement.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array)
        {
            throw new ReviewFailureException("Malformed model response: missing output.");
        }

        foreach (var outputItem in output.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString() ?? string.Empty;
                }
            }
        }

        throw new ReviewFailureException("Malformed model response: missing review text.");
    }

    private static string GetOpenAiErrorMessage(string responseBody, string apiKey)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return RedactSecret(message.GetString() ?? "No error details returned.", apiKey);
            }
        }
        catch (JsonException)
        {
            return "No error details returned.";
        }

        return "No error details returned.";
    }

    private static string RedactSecret(string value, string secret) =>
        string.IsNullOrEmpty(secret) ? value : value.Replace(secret, "<redacted>", StringComparison.Ordinal);

    private static ReviewValidationResult ValidateFindingsAgainstDiff(ReviewResult review, IReadOnlyDictionary<string, HashSet<int>> diffTargets)
    {
        var normalizedFindings = new List<ReviewFinding>();
        var inlineFindings = new List<ReviewFinding>();
        foreach (var finding in review.Findings)
        {
            var file = NormalizeFindingPath(finding.File);
            var normalizedFinding = finding with { File = file };
            normalizedFindings.Add(normalizedFinding);

            if (!diffTargets.TryGetValue(file, out var validLines))
            {
                Console.WriteLine($"Skipped inline comment for unmapped diff location: {finding.File}:{finding.Line} was not found in the PR diff.");
                continue;
            }

            if (!validLines.Contains(finding.Line))
            {
                Console.WriteLine($"Skipped inline comment for unmapped diff location: {finding.File}:{finding.Line} is not a changed right-side line.");
                continue;
            }

            inlineFindings.Add(normalizedFinding);
        }

        if (review.Findings.Count > 0 && inlineFindings.Count == 0)
        {
            Console.WriteLine("No valid inline findings remained after diff validation.");
        }

        return new ReviewValidationResult(new ReviewResult(normalizedFindings, review.Advice), inlineFindings);
    }

    private static DiffInfo ParseUnifiedDiff(string diff)
    {
        var files = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        string? currentFile = null;
        var newLine = 0;
        var inHunk = false;
        var addedLines = 0;

        using var reader = new StringReader(diff);
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                currentFile = NormalizeDiffPath(line[4..]);
                inHunk = false;
                if (currentFile is not null && !files.ContainsKey(currentFile))
                {
                    files[currentFile] = [];
                }

                continue;
            }

            if (currentFile is null)
            {
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                newLine = ParseNewLineStart(line);
                inHunk = newLine > 0;
                continue;
            }

            if (!inHunk || line.Length == 0 || line[0] == '\\')
            {
                continue;
            }

            if (line[0] == '+')
            {
                files[currentFile].Add(newLine);
                addedLines++;
                newLine++;
            }
            else if (line[0] == ' ')
            {
                newLine++;
            }
        }

        return new DiffInfo(files, addedLines);
    }

    private static int ParseNewLineStart(string hunkHeader)
    {
        var plusIndex = hunkHeader.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex < 0)
        {
            return 0;
        }

        var startIndex = plusIndex + 1;
        var endIndex = startIndex;
        while (endIndex < hunkHeader.Length && char.IsDigit(hunkHeader[endIndex]))
        {
            endIndex++;
        }

        return int.TryParse(hunkHeader[startIndex..endIndex], out var start) ? start : 0;
    }

    private static string? NormalizeDiffPath(string path)
    {
        var normalized = path.Trim();
        if (normalized == "/dev/null")
        {
            return null;
        }

        normalized = TrimQuotedPath(normalized).Replace('\\', '/');
        return normalized.StartsWith("a/", StringComparison.Ordinal) || normalized.StartsWith("b/", StringComparison.Ordinal)
            ? normalized[2..]
            : normalized;
    }

    private static string NormalizeFindingPath(string path)
    {
        var normalized = TrimQuotedPath(path.Trim()).Replace('\\', '/');
        return normalized.StartsWith("a/", StringComparison.Ordinal) || normalized.StartsWith("b/", StringComparison.Ordinal)
            ? normalized[2..]
            : normalized;
    }

    private static string TrimQuotedPath(string path) =>
        path.Length >= 2 && path[0] == '"' && path[^1] == '"' ? path[1..^1] : path;

    private static List<GitHubReviewComment> BuildGitHubReviewComments(IReadOnlyCollection<ReviewFinding> findings) =>
        findings
            .Select(finding => new GitHubReviewComment(finding.File, finding.Line, "RIGHT", BuildInlineCommentBody(finding)))
            .ToList();

    private static string BuildInlineCommentBody(ReviewFinding finding) =>
        $"{GetSeverityIcon(finding.Severity)} **{finding.Title.Trim()}**\n\n{finding.Message.Trim()}\n\n**Action:** {finding.Action.Trim()}";

    private static string GetSeverityIcon(string severity) => severity switch
    {
        "high" => "🔴",
        "medium" => "🟡",
        "low" => "🔵",
        _ => "🔵"
    };

    private static async Task PublishGitHubReviewAsync(string report, IReadOnlyCollection<ReviewFinding> inlineFindings)
    {
        var comments = BuildGitHubReviewComments(inlineFindings);
        var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var pullRequestNumber = Environment.GetEnvironmentVariable("PR_NUMBER");
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var isGitHubActions = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
        var missingValues = new List<string>();

        if (string.IsNullOrWhiteSpace(repository))
        {
            missingValues.Add("GITHUB_REPOSITORY");
        }

        if (string.IsNullOrWhiteSpace(pullRequestNumber))
        {
            missingValues.Add("PR_NUMBER");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            missingValues.Add("GITHUB_TOKEN");
        }

        if (missingValues.Count > 0)
        {
            var missingMessage = $"missing {string.Join(", ", missingValues)}";
            if (isGitHubActions)
            {
                throw new ReviewFailureException($"GitHub review publishing failed: {missingMessage}.");
            }

            Console.WriteLine();
            Console.WriteLine($"GitHub review publishing skipped: {missingMessage}.");
            return;
        }

        var repositoryParts = repository!.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (repositoryParts.Length != 2 || !int.TryParse(pullRequestNumber, out var pullNumber) || pullNumber < 1)
        {
            throw new ReviewFailureException("GitHub review publishing failed: invalid GITHUB_REPOSITORY or PR_NUMBER.");
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(GitHubUserAgent);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var owner = Uri.EscapeDataString(repositoryParts[0]);
        var repo = Uri.EscapeDataString(repositoryParts[1]);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GitHubApiRoot}/repos/{owner}/{repo}/pulls/{pullNumber}/reviews")
        {
            Content = new StringContent(CreateGitHubReviewRequestJson(report, comments), Encoding.UTF8, "application/json")
        };

        using var response = await httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new ReviewFailureException($"GitHub review publishing failed ({(int)response.StatusCode} {response.ReasonPhrase}): {GetGitHubErrorMessage(responseBody, token!)}");
        }

        Console.WriteLine();
        Console.WriteLine($"GitHub Pull Request Review published with {comments.Count} inline comment(s).");
    }

    private static string CreateGitHubReviewRequestJson(string report, IReadOnlyCollection<GitHubReviewComment> comments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("body", report);
            writer.WriteString("event", "COMMENT");
            writer.WriteStartArray("comments");

            foreach (var comment in comments)
            {
                writer.WriteStartObject();
                writer.WriteString("path", comment.Path);
                writer.WriteNumber("line", comment.Line);
                writer.WriteString("side", comment.Side);
                writer.WriteString("body", comment.Body);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string GetGitHubErrorMessage(string responseBody, string token)
    {
        var redactedBody = RedactSecret(responseBody, token);
        try
        {
            using var document = JsonDocument.Parse(redactedBody);
            if (document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? "No error details returned.";
            }
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(redactedBody) ? "No error details returned." : redactedBody;
        }

        return string.IsNullOrWhiteSpace(redactedBody) ? "No error details returned." : redactedBody;
    }

    private static string BuildReviewReport(ReviewResult review, int addedLines)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 🤖 Enzo Code Reviewer");
        builder.AppendLine();

        if (review.Findings.Count == 0)
        {
            if (review.Advice.Count > 0)
            {
                AppendAdvice(builder, review.Advice);
            }
            else
            {
                builder.AppendLine("## ✨ Good job!");
                builder.AppendLine();
                builder.AppendLine("No actionable issues or specific recommendations found. 🚀");
            }

            return builder.ToString().TrimEnd();
        }

        builder.AppendLine("## 🔍 Issues Found");
        builder.AppendLine();

        foreach (var severity in new[] { "high", "medium", "low" })
        {
            var findings = review.Findings
                .Where(finding => finding.Severity == severity)
                .ToList();
            if (findings.Count == 0)
            {
                continue;
            }

            builder.AppendLine($"### {GetSeverityIcon(severity)} {severity.ToUpperInvariant()}");
            builder.AppendLine();

            for (var i = 0; i < findings.Count; i++)
            {
                var finding = findings[i];
                builder.AppendLine($"#### {i + 1}. {finding.Title.Trim()}");
                builder.AppendLine();
                builder.AppendLine($"**Location:** `{finding.File}:{finding.Line}`");
                builder.AppendLine();
                builder.AppendLine(finding.Message.Trim());
                builder.AppendLine();
                builder.AppendLine("**Action**");
                builder.AppendLine();
                builder.AppendLine(finding.Action.Trim());
                builder.AppendLine();
                builder.AppendLine("**Agent Prompt**");
                builder.AppendLine();
                AppendBlockQuote(builder, finding.AgentPrompt.Trim());
                builder.AppendLine();
            }

            builder.AppendLine("---");
            builder.AppendLine();
        }

        AppendStatistics(builder, BuildReviewStatistics(review.Findings, addedLines));

        if (review.Advice.Count > 0)
        {
            builder.AppendLine();
            AppendAdvice(builder, review.Advice);
        }

        return builder.ToString().TrimEnd();
    }

    private static ReviewStatistics BuildReviewStatistics(IReadOnlyCollection<ReviewFinding> findings, int addedLines)
    {
        var high = findings.Count(finding => finding.Severity == "high");
        var medium = findings.Count(finding => finding.Severity == "medium");
        var low = findings.Count(finding => finding.Severity == "low");
        return new ReviewStatistics(addedLines, findings.Count, high, medium, low);
    }

    private static void AppendStatistics(StringBuilder builder, ReviewStatistics statistics)
    {
        builder.AppendLine("## 📊 Statistics");
        builder.AppendLine();
        builder.AppendLine($"**Added lines:** {statistics.AddedLines}  ");
        builder.AppendLine($"**Issues found:** {statistics.TotalIssues}  ");
        builder.AppendLine($"**Issue density:** {FormatIssueDensity(statistics)}");
        builder.AppendLine();
        builder.AppendLine("### Severity Distribution");
        builder.AppendLine();
        builder.AppendLine($"- 🔴 High: {statistics.HighCount} — {FormatSeverityPercent(statistics.HighCount, statistics.TotalIssues)}");
        builder.AppendLine($"- 🟡 Medium: {statistics.MediumCount} — {FormatSeverityPercent(statistics.MediumCount, statistics.TotalIssues)}");
        builder.AppendLine($"- 🔵 Low: {statistics.LowCount} — {FormatSeverityPercent(statistics.LowCount, statistics.TotalIssues)}");
        builder.AppendLine();
        builder.AppendLine("```mermaid");
        builder.AppendLine("pie showData");
        builder.AppendLine("    title Issue Severity");
        builder.AppendLine($"    \"High\" : {statistics.HighCount}");
        builder.AppendLine($"    \"Medium\" : {statistics.MediumCount}");
        builder.AppendLine($"    \"Low\" : {statistics.LowCount}");
        builder.AppendLine("```");
    }

    private static void AppendAdvice(StringBuilder builder, IReadOnlyCollection<ReviewAdvice> advice)
    {
        builder.AppendLine("## 💡 Advice");
        builder.AppendLine();
        foreach (var item in advice)
        {
            builder.AppendLine($"- {item.Message.Trim()}");
        }
    }

    private static void AppendBlockQuote(StringBuilder builder, string value)
    {
        using var reader = new StringReader(value);
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            builder.AppendLine($"> {line}");
        }
    }

    private static string FormatIssueDensity(ReviewStatistics statistics) =>
        statistics.AddedLines == 0
            ? "N/A"
            : $"{FormatDecimal(statistics.TotalIssues / (double)statistics.AddedLines * 100)} findings per 100 added lines";

    private static string FormatSeverityPercent(int count, int total) =>
        total == 0 ? "0.0%" : $"{FormatDecimal(count / (double)total * 100)}%";

    private static string FormatDecimal(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static ReviewResult ParseReviewResult(string reviewJson)
    {
        using var document = JsonDocument.Parse(reviewJson);
        var root = document.RootElement;

        if (!root.TryGetProperty("findings", out var findingsElement)
            || findingsElement.ValueKind != JsonValueKind.Array)
        {
            throw new ReviewFailureException("Malformed model response: findings array is missing.");
        }

        var findings = new List<ReviewFinding>();
        foreach (var findingElement in findingsElement.EnumerateArray())
        {
            if (findingElement.ValueKind != JsonValueKind.Object)
            {
                throw new ReviewFailureException("Malformed model response: finding must be an object.");
            }

            findings.Add(new ReviewFinding(
                ReadRequiredString(findingElement, "file"),
                ReadRequiredInt(findingElement, "line"),
                ReadRequiredString(findingElement, "severity"),
                ReadRequiredString(findingElement, "title"),
                ReadRequiredString(findingElement, "message"),
                ReadRequiredString(findingElement, "action"),
                ReadRequiredString(findingElement, "agent_prompt")));
        }

        if (!root.TryGetProperty("advice", out var adviceElement)
            || adviceElement.ValueKind != JsonValueKind.Array)
        {
            throw new ReviewFailureException("Malformed model response: advice array is missing.");
        }

        var advice = new List<ReviewAdvice>();
        foreach (var adviceItem in adviceElement.EnumerateArray())
        {
            if (adviceItem.ValueKind != JsonValueKind.Object)
            {
                throw new ReviewFailureException("Malformed model response: advice must be an object.");
            }

            advice.Add(new ReviewAdvice(ReadRequiredString(adviceItem, "message")));
        }

        return new ReviewResult(findings, advice);
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new ReviewFailureException($"Malformed model response: {propertyName} is missing or invalid.");
        }

        return property.GetString() ?? string.Empty;
    }

    private static int ReadRequiredInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            throw new ReviewFailureException($"Malformed model response: {propertyName} is missing or invalid.");
        }

        return property.GetInt32();
    }

    private static void ValidateReviewResult(ReviewResult review)
    {
        if (review.Findings is null)
        {
            throw new ReviewFailureException("Invalid review result: findings is missing.");
        }

        if (review.Advice is null)
        {
            throw new ReviewFailureException("Invalid review result: advice is missing.");
        }

        for (var i = 0; i < review.Findings.Count; i++)
        {
            var finding = review.Findings[i];
            if (string.IsNullOrWhiteSpace(finding.File))
            {
                throw new ReviewFailureException($"Invalid review finding {i + 1}: file is required.");
            }

            if (finding.Line < 1)
            {
                throw new ReviewFailureException($"Invalid review finding {i + 1}: line must be greater than zero.");
            }

            if (finding.Severity is not ("high" or "medium" or "low"))
            {
                throw new ReviewFailureException($"Invalid review finding {i + 1}: severity must be high, medium, or low.");
            }

            if (string.IsNullOrWhiteSpace(finding.Title)
                || string.IsNullOrWhiteSpace(finding.Message)
                || string.IsNullOrWhiteSpace(finding.Action)
                || string.IsNullOrWhiteSpace(finding.AgentPrompt))
            {
                throw new ReviewFailureException($"Invalid review finding {i + 1}: title, message, action, and agent prompt are required.");
            }
        }

        for (var i = 0; i < review.Advice.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(review.Advice[i].Message))
            {
                throw new ReviewFailureException($"Invalid review advice {i + 1}: message is required.");
            }
        }
    }

    private static void PrintReview(string report)
    {
        Console.WriteLine(report);
    }

    private static int RunSelfTests()
    {
        var diff = """
            diff --git a/src/Foo.cs b/src/Foo.cs
            index 1111111..2222222 100644
            --- a/src/Foo.cs
            +++ b/src/Foo.cs
            @@ -10,4 +10,5 @@ public class Foo
             context one
            -removed old line
            +added first line
             context two
            +added second line
            @@ -30,2 +31,3 @@ public class Foo
             later context
            +later added line
             later context two
            diff --git a/src/Bar.cs b/src/Bar.cs
            index 3333333..4444444 100644
            --- a/src/Bar.cs
            +++ b/src/Bar.cs
            @@ -1,2 +1,3 @@ public class Bar
             bar context
            +bar added line
             bar context two
            diff --git a/src/Deleted.cs b/src/Deleted.cs
            deleted file mode 100644
            index 5555555..0000000
            --- a/src/Deleted.cs
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -deleted one
            -deleted two
            """;

        var diffInfo = ParseUnifiedDiff(diff);
        Assert(diffInfo.AddedLineCount == 4, "added-line counting should include added lines across files and hunks only");
        AssertTarget(diffInfo.ChangedLines, "src/Foo.cs", 11, expected: true, "added line");
        AssertTarget(diffInfo.ChangedLines, "src/Foo.cs", 13, expected: true, "second added line");
        AssertTarget(diffInfo.ChangedLines, "src/Foo.cs", 32, expected: true, "multiple hunks");
        AssertTarget(diffInfo.ChangedLines, "src/Bar.cs", 2, expected: true, "multiple files");
        AssertTarget(diffInfo.ChangedLines, "src/Foo.cs", 10, expected: false, "context line");
        AssertTarget(diffInfo.ChangedLines, "src/Foo.cs", 12, expected: false, "context after deletion");
        AssertTarget(diffInfo.ChangedLines, "src/Foo.cs", 31, expected: false, "later context line");
        AssertTarget(diffInfo.ChangedLines, "src/Deleted.cs", 1, expected: false, "deleted file line");

        var mixed = new ReviewResult([
            new ReviewFinding("src/Foo.cs", 11, "high", "High finding", "Message", "Fix it", "Fix src/Foo.cs around line 11. Preserve behavior and add focused tests."),
            new ReviewFinding("src/Foo.cs", 12, "medium", "Medium invalid line", "Message", "Fix it", "Fix src/Foo.cs around line 12. Preserve behavior and add focused tests."),
            new ReviewFinding("missing/File.cs", 1, "low", "Low invalid file", "Message", "Fix it", "Fix missing/File.cs around line 1. Preserve behavior and add focused tests."),
            new ReviewFinding("b/src/Bar.cs", 2, "low", "Low valid prefixed path", "Message", "Fix it", "Fix src/Bar.cs around line 2. Preserve behavior and add focused tests.")
        ], [new ReviewAdvice("Using AsNoTracking() here would avoid unnecessary EF Core tracking because this query is read-only.")]);

        var valid = ValidateFindingsAgainstDiff(mixed, diffInfo.ChangedLines);
        Assert(valid.Review.Findings.Count == 4, "invalid inline locations should remain in the overall report");
        Assert(valid.InlineFindings.Count == 2, "only valid locations should become inline findings");
        Assert(valid.Review.Findings[3].File == "src/Bar.cs", "finding paths should be normalized in reports");

        var zero = ValidateFindingsAgainstDiff(new ReviewResult([], []), diffInfo.ChangedLines);
        Assert(zero.Review.Findings.Count == 0, "zero findings should remain zero");

        var comments = BuildGitHubReviewComments(valid.InlineFindings);
        Assert(comments.Count == 2, "valid findings should become inline comments");
        Assert(comments.All(comment => comment.Side == "RIGHT"), "inline comments should target the right side of the diff");

        var report = BuildReviewReport(valid.Review, diffInfo.AddedLineCount);
        Assert(report.Contains("## 🔍 Issues Found", StringComparison.Ordinal), "report should include issues section when findings exist");
        Assert(report.Contains("### 🔴 HIGH", StringComparison.Ordinal), "report should include high findings");
        Assert(report.Contains("### 🟡 MEDIUM", StringComparison.Ordinal), "report should include medium findings");
        Assert(report.Contains("### 🔵 LOW", StringComparison.Ordinal), "report should include low findings");
        Assert(report.Contains("#### 1. High finding", StringComparison.Ordinal), "numbering should start at one for high severity");
        Assert(report.Contains("#### 1. Medium invalid line", StringComparison.Ordinal), "numbering should reset for medium severity");
        Assert(report.Contains("#### 1. Low invalid file", StringComparison.Ordinal), "numbering should reset for low severity");
        Assert(report.Contains("**Action**", StringComparison.Ordinal), "each issue should show an action");
        Assert(report.Contains("**Agent Prompt**", StringComparison.Ordinal), "each issue should show an agent prompt");
        Assert(report.Contains("**Added lines:** 4", StringComparison.Ordinal), "statistics should show added lines");
        Assert(report.Contains("**Issues found:** 4", StringComparison.Ordinal), "statistics should show issue count");
        Assert(report.Contains("**Issue density:** 100.0 findings per 100 added lines", StringComparison.Ordinal), "statistics should show issue density");
        Assert(report.Contains("🔴 High: 1 — 25.0%", StringComparison.Ordinal), "statistics should show high percentage");
        Assert(report.Contains("🟡 Medium: 1 — 25.0%", StringComparison.Ordinal), "statistics should show medium percentage");
        Assert(report.Contains("🔵 Low: 2 — 50.0%", StringComparison.Ordinal), "statistics should show low percentage");
        Assert(report.Contains("```mermaid", StringComparison.Ordinal), "statistics should include Mermaid chart");
        Assert(report.Contains("## 💡 Advice", StringComparison.Ordinal), "report should include advice when findings and advice exist");

        var noMedium = BuildReviewReport(new ReviewResult([
            new ReviewFinding("src/Foo.cs", 11, "high", "Only high", "Message", "Action", "Prompt"),
            new ReviewFinding("src/Bar.cs", 2, "low", "Only low", "Message", "Action", "Prompt")
        ], []), diffInfo.AddedLineCount);
        Assert(!noMedium.Contains("### 🟡 MEDIUM", StringComparison.Ordinal), "severity sections with zero findings should be omitted");

        var zeroAddedReport = BuildReviewReport(new ReviewResult([
            new ReviewFinding("src/Foo.cs", 11, "high", "Zero added", "Message", "Action", "Prompt")
        ], []), 0);
        Assert(zeroAddedReport.Contains("**Issue density:** N/A", StringComparison.Ordinal), "zero added lines should avoid density division");

        var adviceOnlyReport = BuildReviewReport(new ReviewResult([], [new ReviewAdvice("Use the built-in framework API here to reduce custom code.")]), diffInfo.AddedLineCount);
        Assert(!adviceOnlyReport.Contains("## 🔍 Issues Found", StringComparison.Ordinal), "advice-only report should omit issues");
        Assert(!adviceOnlyReport.Contains("## 📊 Statistics", StringComparison.Ordinal), "advice-only report should omit statistics");
        Assert(adviceOnlyReport.Contains("## 💡 Advice", StringComparison.Ordinal), "advice-only report should include advice");

        var goodJobReport = BuildReviewReport(new ReviewResult([], []), diffInfo.AddedLineCount);
        Assert(goodJobReport.Contains("## ✨ Good job!", StringComparison.Ordinal), "empty review should include good job fallback");
        Assert(!goodJobReport.Contains("## 📊 Statistics", StringComparison.Ordinal), "empty review should omit statistics");

        Console.WriteLine("Self-tests passed.");
        return 0;
    }

    private static void AssertTarget(IReadOnlyDictionary<string, HashSet<int>> targets, string file, int line, bool expected, string scenario)
    {
        var actual = targets.TryGetValue(file, out var lines) && lines.Contains(line);
        Assert(actual == expected, $"Unexpected target validity for {scenario}: {file}:{line}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new ReviewFailureException($"Self-test failed: {message}");
        }
    }
}

sealed class ReviewFailureException(string message) : Exception(message);

record ReviewOptions(string DiffPath, string SkillsDirectory);

record ReviewSkill(string Name, string Content);

record DiffInfo(Dictionary<string, HashSet<int>> ChangedLines, int AddedLineCount);

record ReviewValidationResult(ReviewResult Review, List<ReviewFinding> InlineFindings);

record ReviewResult(List<ReviewFinding> Findings, List<ReviewAdvice> Advice);

record ReviewFinding(
    string File,
    int Line,
    string Severity,
    string Title,
    string Message,
    string Action,
    string AgentPrompt);

record ReviewAdvice(string Message);

record ReviewStatistics(int AddedLines, int TotalIssues, int HighCount, int MediumCount, int LowCount);

record GitHubReviewComment(string Path, int Line, string Side, string Body);
