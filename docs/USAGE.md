# 📖 PR Bot — Detailed Usage Guide

This guide explains how to install, configure, and customize the Universal PR Bot across your repositories.

---

## 1. Quick Integration

Add this file to any repository at `.github/workflows/pr-review.yml`:

```yaml
name: PR Review Bot

on:
  pull_request:
    types: [opened, synchronize, reopened]

permissions:
  contents: read
  pull-requests: write

jobs:
  review:
    runs-on: ubuntu-latest
    steps:
      - name: Automated Code Review
        uses: YOUR_GITHUB_USERNAME/PR_Bot@main
        with:
          github-token: ${{ secrets.GITHUB_TOKEN }}
          gemini-api-key: ${{ secrets.GEMINI_API_KEY }}
```

---

## 2. Configuration Options (Action Inputs)

The following inputs are supported by `action.yml`:

| Input | Required | Default | Description |
|---|:---:|:---:|---|
| `github-token` | **Yes** | `${{ github.token }}` | GitHub token used to fetch PR diffs and post comments. |
| `gemini-api-key` | No | `""` | Google Gemini API key from AI Studio. If omitted, only rule-based checks run. |
| `gemini-model` | No | `gemini-2.5-flash` | The Gemini model name to use (e.g., `gemini-2.5-flash`, `gemini-2.5-pro`, `gemini-1.5-flash`). |
| `fail-on-errors` | No | `false` | If set to `true`, the GitHub workflow check fails if critical errors (e.g. hardcoded secrets) are detected. |
| `dotnet-version` | No | `10.0.x` | The .NET SDK version to install on the Actions runner. |

### Example with Custom Inputs:

```yaml
      - name: Automated Code Review
        uses: YOUR_GITHUB_USERNAME/PR_Bot@main
        with:
          github-token: ${{ secrets.GITHUB_TOKEN }}
          gemini-api-key: ${{ secrets.GEMINI_API_KEY }}
          gemini-model: 'gemini-2.5-pro'
          fail-on-errors: 'true'
```

---

## 3. Important GitHub Actions Permissions

Ensure your workflow includes the following permissions block:

```yaml
permissions:
  contents: read
  pull-requests: write
```

- **`contents: read`**: Required to read repository file diffs.
- **`pull-requests: write`**: Required to post review comments on the pull request.

---

## 4. Handling Public Repositories & External Forks

By default, GitHub's `pull_request` event provides a **read-only** token when a PR originates from an external fork (to protect secrets like `GEMINI_API_KEY`).

If you want the bot to comment on PRs submitted by outside contributors from forks:
1. Use the `pull_request_target` trigger instead of `pull_request`:
```yaml
on:
  pull_request_target:
    types: [opened, synchronize, reopened]
```
> ⚠️ **Security Tip:** When using `pull_request_target`, never check out and execute untrusted code directly in the runner without review. PR Bot only analyzes the git diff via the GitHub API, keeping your runner safe.

---

## 5. Using as a C# Library in Your Own Code

You can also embed the core review engine directly into your own ASP.NET Core apps, CLI tools, or services:

```csharp
using Microsoft.Extensions.DependencyInjection;
using PR_Bot.Core.Extensions;
using PR_Bot.Analyzers.Gemini.Extensions;
using PR_Bot.Core.Services;

var services = new ServiceCollection();

// Register Core services and analyzers
services.AddPrBotCore(options =>
{
    options.MaxLineLength = 100;
    options.MaxFilesPerPr = 30;
});

// Register Gemini AI analyzer
services.AddGeminiAnalyzer(options =>
{
    options.ApiKey = "YOUR_GEMINI_API_KEY";
    options.Model = "gemini-2.5-flash";
});

var provider = services.BuildServiceProvider();
var engine = provider.GetRequiredService<IReviewEngine>();

// Run review
var result = await engine.RunAsync(pullRequestContext);
```
