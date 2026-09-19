# Enzo.Helpers.CodeReviewer

## Overview

`Enzo.Helpers.CodeReviewer` is a lightweight AI-assisted code reviewer implemented as a .NET 10 file-based C# application.

It reviews pull request diffs using external review skills and OpenAI, validates the structured model response, prints findings to stdout, and posts the findings as an advisory GitHub Pull Request Review using the Enzo Code Reviewer GitHub App identity.

## Architecture

```text
Consuming repository
        |
        | OPENAI_API_KEY
        | Enzo Code Reviewer GitHub App ID
        | Enzo Code Reviewer GitHub App private key
        v
Reusable GitHub Actions workflow
        |
        | creates installation token
        v
Code Reviewer
        |
        | sends PR diff and review skills
        v
OpenAI
        |
        | returns structured review findings
        v
GitHub Pull Request Review
```

The GitHub review is always submitted with the `COMMENT` event. It does not approve PRs or request changes.

OpenAI performs the review analysis. `Enzo.Ai.Skills` provides external review guidance. The Enzo Code Reviewer GitHub App provides the GitHub identity used to publish PR reviews.

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

For review analysis, the reviewer reads OpenAI credentials from `OPENAI_API_KEY`.

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

Local review generation does not require GitHub App credentials. If GitHub publishing context is unavailable, the reviewer prints the review and skips publishing as before.

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

`Enzo.Helpers.CodeReviewer` is intended to run as a reusable GitHub Actions workflow. Consuming repositories decide when to call it.

Recommended caller workflow:

```yaml
name: Enzo Code Review

on:
  pull_request:
    types:
      - opened

permissions:
  contents: read
  pull-requests: write

jobs:
  review:
    uses: solakpetri/Enzo.Helpers.CodeReviewer/.github/workflows/review.yml@main
    with:
      ENZO_CODE_REVIEWER_APP_ID: ${{ vars.ENZO_CODE_REVIEWER_APP_ID }}
    secrets:
      OPENAI_API_KEY: ${{ secrets.OPENAI_API_KEY }}
      ENZO_CODE_REVIEWER_PRIVATE_KEY: ${{ secrets.ENZO_CODE_REVIEWER_PRIVATE_KEY }}
```

Automatic review is recommended only for `pull_request: opened`:

```text
PR created
    -> review automatically runs once

additional commit pushed
    -> nothing

user wants another review
    -> GitHub Actions -> Re-run jobs
```

The reusable workflow:

```text
checks out source
-> checks out skills
-> generates diff
-> creates an Enzo Code Reviewer GitHub App installation token
-> runs reviewer
-> prints findings
-> posts a COMMENT pull request review as the Enzo Code Reviewer GitHub App
```

It uses GitHub-hosted `ubuntu-latest`, sets up .NET 10, checks out `Enzo.Ai.Skills` with `actions/checkout`, generates the PR diff, and runs:

```bash
dotnet reviewer/reviewer.cs changes.diff --skills skills
```

The diff is generated from the pull request base SHA to the pull request head SHA, so the reviewer receives only PR changes. The generated diff file is not committed.

The reusable workflow grants only the permissions needed to read repository contents and write pull request reviews:

```yaml
permissions:
  contents: read
  pull-requests: write
```

Consuming repositories should grant the same permissions to the caller workflow. The Enzo Code Reviewer GitHub App installation must also have repository contents read access and pull request write access for the target repository.

## Reusable Workflow Contract

The reusable workflow accepts one non-secret input:

```text
ENZO_CODE_REVIEWER_APP_ID
```

Store the App ID as a repository or organization variable, for example `vars.ENZO_CODE_REVIEWER_APP_ID`.

The reusable workflow explicitly accepts these secrets:

```text
OPENAI_API_KEY
ENZO_CODE_REVIEWER_PRIVATE_KEY
```

Do not use `secrets: inherit`. Pass only the required secrets explicitly.

The GitHub App private key must be stored as a GitHub Actions secret and must never be committed. Consuming repositories provide their own `OPENAI_API_KEY`.

## Authentication

The workflow creates a short-lived installation token with `actions/create-github-app-token@v2` using:

```text
ENZO_CODE_REVIEWER_APP_ID
ENZO_CODE_REVIEWER_PRIVATE_KEY
```

The private key is provided only to the token-generation step. The generated installation token is passed to the reviewer as `GITHUB_TOKEN`:

```yaml
GITHUB_TOKEN: ${{ steps.app-token.outputs.token }}
```

The reviewer does not understand GitHub App authentication. It only uses `GITHUB_TOKEN` to publish the existing GitHub Pull Request Review request.

The OpenAI API key is passed to the reviewer unchanged:

```yaml
OPENAI_API_KEY: ${{ secrets.OPENAI_API_KEY }}
```

Do not commit API keys, private keys, installation tokens, `.env` files containing credentials, or credential-containing application settings. PR reviews appear under the Enzo Code Reviewer GitHub App identity.

## Current Limitations

The current version:

- reviews PR diffs
- loads external skills
- runs through a reusable GitHub Actions workflow
- prints findings to workflow logs
- posts a GitHub Pull Request Review using `COMMENT` as the Enzo Code Reviewer GitHub App
- never approves PRs or requests changes

Current limitations:

- create inline review comments
- manage duplicate reviews

## Roadmap

Duplicate-review handling is planned for a later version.

Later milestone: inline review comments.
