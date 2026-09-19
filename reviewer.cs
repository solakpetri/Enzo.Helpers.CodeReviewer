#nullable enable

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
            var diffTargets = ParseUnifiedDiff(diff);
            var skills = await LoadSkillsAsync(options.SkillsDirectory);
            var apiKey = GetApiKey();
            var prompt = BuildReviewPrompt(skills, diff);
            var reviewJson = await RequestReviewJsonAsync(apiKey, prompt);
            var review = ParseReviewResult(reviewJson);

            ValidateReviewResult(review);
            var validReview = ValidateFindingsAgainstDiff(review, diffTargets);
            PrintReview(validReview);
            await PublishGitHubReviewAsync(validReview.Findings);
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
        builder.AppendLine("Return only concrete, actionable problems introduced or exposed by the pull request. If there are no actionable issues, return an empty findings array.");
        builder.AppendLine("Prioritize correctness bugs, security vulnerabilities, concurrency problems, race conditions, async misuse, resource leaks, EF Core misuse, database consistency issues, broken error handling, meaningful architectural violations, important missing validation, significant performance problems, and missing tests where changed behavior creates meaningful regression risk.");
        builder.AppendLine("Do not report praise, positive observations, code summaries, things the code does correctly, compliments, stylistic preferences, formatting, naming trivia, subjective refactoring preferences, optional improvements without a concrete benefit, unchanged code, speculative problems without a plausible failure mode, or comments merely to demonstrate inspection.");
        builder.AppendLine("Every finding must represent something the developer should reasonably consider fixing. Do not manufacture findings so the review has content.");
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
                "required": ["findings"],
                "properties": {
                  "findings": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["file", "line", "severity", "title", "message", "suggestion"],
                      "properties": {
                        "file": { "type": "string" },
                        "line": { "type": "integer", "minimum": 1 },
                        "severity": { "type": "string", "enum": ["high", "medium", "low"] },
                        "title": { "type": "string" },
                        "message": { "type": "string" },
                        "suggestion": { "type": "string" }
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

    private static ReviewResult ValidateFindingsAgainstDiff(ReviewResult review, IReadOnlyDictionary<string, HashSet<int>> diffTargets)
    {
        if (review.Findings.Count == 0)
        {
            return review;
        }

        var validFindings = new List<ReviewFinding>();
        foreach (var finding in review.Findings)
        {
            var file = NormalizeFindingPath(finding.File);
            if (!diffTargets.TryGetValue(file, out var validLines))
            {
                Console.WriteLine($"Skipped finding with unmapped diff location: {finding.File}:{finding.Line} was not found in the PR diff.");
                continue;
            }

            if (!validLines.Contains(finding.Line))
            {
                Console.WriteLine($"Skipped finding with unmapped diff location: {finding.File}:{finding.Line} is not a changed right-side line.");
                continue;
            }

            validFindings.Add(finding with { File = file });
        }

        if (validFindings.Count == 0)
        {
            Console.WriteLine("No valid inline findings remained after diff validation.");
        }

        return new ReviewResult(validFindings);
    }

    private static Dictionary<string, HashSet<int>> ParseUnifiedDiff(string diff)
    {
        var files = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        string? currentFile = null;
        var newLine = 0;
        var inHunk = false;

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
                newLine++;
            }
            else if (line[0] == ' ')
            {
                newLine++;
            }
        }

        return files;
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
        $"{GetSeverityIcon(finding.Severity)} **{finding.Title.Trim()}**\n\n{finding.Message.Trim()}\n\n**Suggestion:** {finding.Suggestion.Trim()}";

    private static string GetSeverityIcon(string severity) => severity switch
    {
        "high" => "🔴",
        "medium" => "🟡",
        "low" => "🔵",
        _ => "🔵"
    };

    private static async Task PublishGitHubReviewAsync(IReadOnlyCollection<ReviewFinding> findings)
    {
        if (findings.Count == 0)
        {
            return;
        }

        var comments = BuildGitHubReviewComments(findings);
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
            Content = new StringContent(CreateGitHubReviewRequestJson(comments), Encoding.UTF8, "application/json")
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

    private static string CreateGitHubReviewRequestJson(IReadOnlyCollection<GitHubReviewComment> comments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
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
                ReadRequiredString(findingElement, "suggestion")));
        }

        return new ReviewResult(findings);
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
                || string.IsNullOrWhiteSpace(finding.Suggestion))
            {
                throw new ReviewFailureException($"Invalid review finding {i + 1}: title, message, and suggestion are required.");
            }
        }
    }

    private static void PrintReview(ReviewResult review)
    {
        Console.WriteLine("AI Code Review");
        Console.WriteLine();

        if (review.Findings.Count == 0)
        {
            Console.WriteLine("No actionable issues found.");
            return;
        }

        foreach (var finding in review.Findings)
        {
            Console.WriteLine($"[{finding.Severity.ToUpperInvariant()}] {finding.File}:{finding.Line}");
            Console.WriteLine(finding.Title);
            Console.WriteLine();
            Console.WriteLine(finding.Message);
            Console.WriteLine();
            Console.WriteLine("Suggestion:");
            Console.WriteLine(finding.Suggestion);
            Console.WriteLine();
        }
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

        var targets = ParseUnifiedDiff(diff);
        AssertTarget(targets, "src/Foo.cs", 11, expected: true, "added line");
        AssertTarget(targets, "src/Foo.cs", 13, expected: true, "second added line");
        AssertTarget(targets, "src/Foo.cs", 32, expected: true, "multiple hunks");
        AssertTarget(targets, "src/Bar.cs", 2, expected: true, "multiple files");
        AssertTarget(targets, "src/Foo.cs", 10, expected: false, "context line");
        AssertTarget(targets, "src/Foo.cs", 12, expected: false, "context after deletion");
        AssertTarget(targets, "src/Foo.cs", 31, expected: false, "later context line");
        AssertTarget(targets, "src/Deleted.cs", 1, expected: false, "deleted file line");

        var mixed = new ReviewResult([
            new ReviewFinding("src/Foo.cs", 11, "high", "Valid", "Message", "Suggestion"),
            new ReviewFinding("src/Foo.cs", 12, "medium", "Invalid line", "Message", "Suggestion"),
            new ReviewFinding("missing/File.cs", 1, "low", "Invalid file", "Message", "Suggestion"),
            new ReviewFinding("b/src/Bar.cs", 2, "low", "Valid prefixed path", "Message", "Suggestion")
        ]);

        var valid = ValidateFindingsAgainstDiff(mixed, targets);
        Assert(valid.Findings.Count == 2, "mixture of valid and invalid findings should keep only valid entries");
        Assert(valid.Findings[1].File == "src/Bar.cs", "finding paths should be normalized for GitHub comments");

        var zero = ValidateFindingsAgainstDiff(new ReviewResult([]), targets);
        Assert(zero.Findings.Count == 0, "zero findings should remain zero");

        var comments = BuildGitHubReviewComments(valid.Findings);
        Assert(comments.Count == 2, "valid findings should become inline comments");
        Assert(comments.All(comment => comment.Side == "RIGHT"), "inline comments should target the right side of the diff");

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

record ReviewResult(List<ReviewFinding> Findings);

record ReviewFinding(
    string File,
    int Line,
    string Severity,
    string Title,
    string Message,
    string Suggestion);

record GitHubReviewComment(string Path, int Line, string Side, string Body);
