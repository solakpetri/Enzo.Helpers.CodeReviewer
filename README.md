# Enzo.Helpers.CodeReviewer

## Overview

`Enzo.Helpers.CodeReviewer` is a lightweight AI-assisted code reviewer implemented as a .NET 10 file-based C# application.

It reviews unified diffs using external review skills and OpenAI, validates the structured model response, and prints findings to stdout. In GitHub Actions, stdout is visible in the workflow log.

## Architecture

```text
GitHub Pull Request
        |
        v
GitHub Actions
        |
        v
PR diff
        |
        v
Enzo.Helpers.CodeReviewer
        |
        v
Enzo.Ai.Skills
        |
        v
OpenAI
        |
        v
Review findings
```

The current version outputs findings to the GitHub Actions log. PR comments and inline review comments are not implemented yet.

## Enzo.Ai.Skills

Review instructions are maintained separately in the public `Enzo.Ai.Skills` repository.

The reviewer is not coupled to how that repository is obtained. It only receives a filesystem path and recursively discovers files named `SKILL.md`.

Expected convention:

```text
Enzo.Ai.Skills/
├── dotnet-backend/
│   └── SKILL.md
├── dotnet-testing/
│   └── SKILL.md
└── ef-core/
    └── SKILL.md
```

Those skill names are examples only. Nested directories are supported, and all discovered `SKILL.md` files are loaded.

## Local Setup

Recommended local workspace:

```text
workspace/
├── Enzo.Helpers.CodeReviewer/
└── Enzo.Ai.Skills/
```

Requirements:

- .NET 10 SDK with C# file-based app support
- A unified diff file to review
- External skills containing at least one `SKILL.md`
- An OpenAI API key provided through `OPENAI_API_KEY`

## OpenAI Configuration

The reviewer reads credentials only from `OPENAI_API_KEY`.

PowerShell:

```powershell
$env:OPENAI_API_KEY="<your-api-key>"
```

Bash:

```bash
export OPENAI_API_KEY="<your-api-key>"
```

Do not store API keys in repository files.

## Local Usage

Run from the `Enzo.Helpers.CodeReviewer` directory:

```bash
dotnet reviewer.cs changes.diff --skills ../Enzo.Ai.Skills
```

`changes.diff` is an existing unified diff file, such as output from `git diff`.

`--skills` points to the external skills directory that contains `SKILL.md` files.

Example output:

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

## GitHub Actions

Pull requests automatically trigger `.github/workflows/review.yml` when they are:

- opened
- synchronized with new commits
- reopened

The workflow:

```text
checks out source
-> checks out skills
-> generates diff
-> runs reviewer
-> prints findings
```

It uses GitHub-hosted `ubuntu-latest`, sets up .NET 10, checks out `Enzo.Ai.Skills` with `actions/checkout`, generates the PR diff in `$RUNNER_TEMP/changes.diff`, and runs:

```bash
dotnet reviewer.cs "$RUNNER_TEMP/changes.diff" --skills ../Enzo.Ai.Skills
```

The diff is generated from the pull request base SHA to the pull request head SHA, so the reviewer receives only PR changes. The generated diff file is temporary and is not committed.

## GitHub Secret

Configure this repository secret under GitHub Actions secrets:

```text
OPENAI_API_KEY
```

The workflow passes it to the reviewer as:

```yaml
OPENAI_API_KEY: ${{ secrets.OPENAI_API_KEY }}
```

Do not commit the key or place it in repository files.

## Current Limitations

The current version:

- reviews PR diffs
- loads external skills
- runs automatically through GitHub Actions
- prints findings to workflow logs

It does not yet:

- post PR comments
- create inline review comments
- approve PRs
- request changes

## Roadmap

Next milestone: GitHub Pull Request review publishing.

Later milestone: inline review comments.
