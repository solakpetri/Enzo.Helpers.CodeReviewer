using System.Text.Json;
using System.Text.Json.Serialization;

return await ReviewerApp.RunAsync(args);

static class ReviewerApp
{
    public static Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: dotnet reviewer.cs <diff-file>");
            return Task.FromResult(1);
        }

        var diffPath = Path.GetFullPath(args[0]);
        if (!File.Exists(diffPath))
        {
            Console.Error.WriteLine($"Diff file not found: {diffPath}");
            return Task.FromResult(1);
        }

        Console.WriteLine("AI Code Review");
        Console.WriteLine();
        Console.WriteLine("Reviewer implementation will be added in the next commit.");
        return Task.FromResult(0);
    }
}
