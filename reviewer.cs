#nullable enable

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

return await ReviewerApp.RunAsync(args);

static class ReviewerApp
{
    private const string OpenAiEndpoint = "https://api.openai.com/v1/responses";
    private const string Model = "gpt-4.1-mini";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var diffPath = GetDiffPath(args);
            var diff = await File.ReadAllTextAsync(diffPath);
            var skills = await LoadSkillsAsync(Path.Combine(Directory.GetCurrentDirectory(), "skills"));
            var apiKey = GetApiKey();
            var prompt = BuildReviewPrompt(skills, diff);
            var reviewJson = await RequestReviewJsonAsync(apiKey, prompt);

            Console.WriteLine("AI Code Review");
            Console.WriteLine();
            Console.WriteLine(reviewJson);
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

    private static string GetDiffPath(string[] args)
    {
        if (args.Length != 1)
        {
            throw new ReviewFailureException("Usage: dotnet reviewer.cs <diff-file>");
        }

        var diffPath = Path.GetFullPath(args[0]);
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

        var skillFiles = Directory.GetFiles(skillsDirectory, "*.md").OrderBy(Path.GetFileName).ToArray();
        if (skillFiles.Length == 0)
        {
            throw new ReviewFailureException($"No Markdown skills found in: {skillsDirectory}");
        }

        var skills = new List<ReviewSkill>();
        foreach (var skillFile in skillFiles)
        {
            var content = await File.ReadAllTextAsync(skillFile);
            if (!string.IsNullOrWhiteSpace(content))
            {
                skills.Add(new ReviewSkill(Path.GetFileName(skillFile), content.Trim()));
            }
        }

        if (skills.Count == 0)
        {
            throw new ReviewFailureException($"No non-empty Markdown skills found in: {skillsDirectory}");
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
        builder.AppendLine("Review only the supplied diff. Focus on evidence in changed lines and their immediate context.");
        builder.AppendLine("Prioritize correctness bugs, security vulnerabilities, concurrency problems, async/await misuse, resource leaks, EF Core misuse, database consistency issues, architectural problems, significant maintainability problems, and missing important tests.");
        builder.AppendLine("Avoid subjective formatting suggestions, personal coding preferences, trivial naming comments, comments about unchanged code, and speculative issues without evidence from the diff.");
        builder.AppendLine("Return only findings that are significant enough for a human reviewer to act on.");
        builder.AppendLine();
        builder.AppendLine("Severity must be exactly one of: high, medium, low.");
        builder.AppendLine("Line must refer to the changed line in the new file when possible. Use the closest changed line when the issue spans multiple lines.");
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
            throw new ReviewFailureException($"OpenAI request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {GetOpenAiErrorMessage(responseBody)}");
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

    private static string GetOpenAiErrorMessage(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? "No error details returned.";
            }
        }
        catch (JsonException)
        {
            return "No error details returned.";
        }

        return "No error details returned.";
    }
}

sealed class ReviewFailureException(string message) : Exception(message);

record ReviewSkill(string Name, string Content);
