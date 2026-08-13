#nullable enable

using System.Text;

return await ReviewerApp.RunAsync(args);

static class ReviewerApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var diffPath = GetDiffPath(args);
            var diff = await File.ReadAllTextAsync(diffPath);
            var skills = await LoadSkillsAsync(Path.Combine(Directory.GetCurrentDirectory(), "skills"));
            var prompt = BuildReviewPrompt(skills, diff);

            Console.WriteLine("AI Code Review");
            Console.WriteLine();
            Console.WriteLine($"Loaded {skills.Count} skills and built a {prompt.Length:N0}-character review prompt.");
            return 0;
        }
        catch (ReviewFailureException ex)
        {
            Console.Error.WriteLine(ex.Message);
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
}

sealed class ReviewFailureException(string message) : Exception(message);

record ReviewSkill(string Name, string Content);
