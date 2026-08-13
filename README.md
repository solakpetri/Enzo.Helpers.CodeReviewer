# Enzo.Helpers.CodeReviewer

AI-assisted local code review for unified diff files.

This first version is a .NET 10 C# file-based CLI. It reads a local diff, loads Markdown review skills, asks OpenAI for structured findings, validates the response, and prints a readable review to stdout.

GitHub Actions and GitHub Pull Request automation will be added later.

## Requirements

- .NET 10 SDK with C# file-based app support
- An OpenAI API key provided through `OPENAI_API_KEY`
- A unified diff file to review

## Configure Credentials

The reviewer reads credentials only from `OPENAI_API_KEY`.

Bash:

```bash
export OPENAI_API_KEY="<your-api-key>"
```

PowerShell:

```powershell
$env:OPENAI_API_KEY="<your-api-key>"
```

Do not store API keys in repository files.

## Run

```bash
dotnet reviewer.cs changes.diff
```

The diff file must exist and should contain the changes to review in unified diff format, such as output from `git diff`.

## Example Output

```text
AI Code Review

[HIGH] src/Repositories/UserRepository.cs:42
Concurrent DbContext usage

Multiple EF Core operations are being started concurrently on the same DbContext.

Suggestion:
Execute them sequentially or use independent DbContext instances.
```

If no significant issues are found:

```text
AI Code Review

No significant issues found.
```

## Project Structure

```text
Enzo.Helpers.CodeReviewer/
├── reviewer.cs
├── README.md
├── .gitignore
└── skills/
    ├── dotnet-backend.md
    ├── dotnet-testing.md
    └── ef-core.md
```

## Review Scope

The reviewer focuses on meaningful engineering issues in the supplied changes, including correctness, security, concurrency, async/await misuse, resource leaks, EF Core misuse, database consistency, maintainability, and important missing tests.

It avoids subjective formatting suggestions, trivial naming comments, unchanged-code comments, and speculative issues without evidence in the diff.
