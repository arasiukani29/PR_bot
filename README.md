# 🤖 Universal GitHub PR Review Bot

A reusable, serverless, automated Pull Request review bot built in **C# (.NET 10)** that works on **any GitHub repository**. It combines static security and code quality checks with **Google Gemini AI** code reviews — running directly inside **GitHub Actions** with **zero server infrastructure**.

---

## 🌟 Features

- **🌐 Universal Reusability:** Call it from any repo using a simple GitHub Actions workflow file (`uses: YOUR_USERNAME/PR_Bot@main`).
- **🧠 AI-Powered Code Review:** Powered by **Google Gemini** (`gemini-3.5-flash` or any Gemini model) to detect logic bugs, unhandled edge cases, security hazards, and performance bottlenecks.
- **🩺 Code Health Score (0-100%):** Automatically computes an objective PR Health Score with visual progress bars and letter grades (`A`, `B`, `C`, `F`).
- **🎯 Priority-Based Findings:** Findings are classified into 🔴 **High Priority** (blocking), 🟡 **Medium Priority** (recommended), and 🟢 **Low Priority** (suggestions).
- **💬 Dual Comments (Overall + Inline):** Posts a comprehensive overview on the PR conversation tab plus inline diff comments directly on affected lines.
- **⚡ Zero Infrastructure:** Runs on GitHub-hosted runners using the built-in `GITHUB_TOKEN`. No webhooks to configure, no Docker containers to maintain, no cloud bills.

---

## 🚀 How to Use in Any Repository (2 Minutes Setup)

### Step 1: Add your Gemini API Key Secret (Optional, for AI Review)
1. Get a free API key from [Google AI Studio](https://aistudio.google.com/apikey).
2. In your target repository on GitHub:
   - Go to **Settings** → **Secrets and variables** → **Actions**.
   - Click **New repository secret**.
   - Name: `GEMINI_API_KEY`
   - Secret: *(Paste your Gemini key)*

*(Note: If no Gemini key is provided, the bot will still run all static analyzers!)*

---

### Step 2: Add the Workflow File
In your repository, create `.github/workflows/pr-review.yml`:

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

> **That's it!** Whenever anyone opens or updates a Pull Request in that repository, the PR Bot will automatically analyze the code diff and post a review summary comment.

---

## 📐 Architecture

```mermaid
flowchart TD
    subgraph Repo["Any Target Repository"]
        PR["Pull Request Created / Updated"]
        WF[".github/workflows/pr-review.yml"]
    end

    subgraph Action["PR_Bot Action (GitHub Runner)"]
        CLI["PR_Bot.Action Console App"]
        OCTO["Octokit GitHub API Client"]
        DIFF["PR Diff Fetcher & Filter"]
        ENGINE["ReviewEngine Orchestrator"]
    end

    subgraph Analyzers["Review Analyzers"]
        SEC["SecurityAnalyzer<br/>(Keys, Secrets, SSL)"]
        STYLE["StyleAnalyzer<br/>(Length, Whitespace)"]
        TODO["TodoAnalyzer<br/>(TODO, FIXME, HACK)"]
        SIZE["FileSizeAnalyzer<br/>(PR Scope & Limits)"]
        GEM["GeminiReviewAnalyzer<br/>(Google Gemini AI)"]
    end

    PR -->|"Triggers"| WF
    WF --> CLI
    CLI --> OCTO --> DIFF
    DIFF --> ENGINE
    ENGINE --> SEC & STYLE & TODO & SIZE & GEM
    ENGINE -->|"Aggregate Review Result"| CLI
    CLI -->|"Post Review Comment"| PR
```

---

## 📋 Review Architecture & Priority System

| Priority | Impact on Health Score | Scope | Examples |
|---|:---:|---|---|
| 🔴 **High Priority** | -25 pts | Must resolve before merge | Critical bugs, SQL/Command injection, exposed secrets/API keys, memory leaks, null reference hazards, auth gaps. |
| 🟡 **Medium Priority** | -10 pts | Recommended to fix | Architectural flaws, N+1 database queries, blocking synchronous calls, unhandled edge cases, missing error handling. |
| 🟢 **Low Priority** | -3 pts | Optional improvement | Non-critical performance optimizations, minor code smells, conventions, readability enhancements. |

---

## 🛠️ Project Structure

```
d:\PR_bot\
├── action.yml                         # Reusable GitHub Action manifest
├── PR_Bot.sln                         # Visual Studio / .NET Solution
│
├── src/
│   ├── PR_Bot.Core/                   # Core engine & pluggable analyzers
│   │   ├── Analyzers/                 # IReviewAnalyzer, Style, Security, etc.
│   │   ├── Configuration/             # PrBotOptions
│   │   ├── Extensions/                # Dependency injection setup
│   │   ├── Models/                    # PullRequestContext, ReviewComment, ReviewResult
│   │   └── Services/                  # GitHubService, ReviewEngine
│   │
│   ├── PR_Bot.Analyzers.Gemini/       # Google Gemini AI code review analyzer
│   │   ├── GeminiApiClient.cs         # Lightweight REST API client
│   │   ├── GeminiOptions.cs           # API configuration
│   │   ├── GeminiReviewAnalyzer.cs    # Analyzer implementation & chunking
│   │   └── Prompts/                   # Structured JSON prompt instructions
│   │
│   └── PR_Bot.Action/                 # GitHub Action console entry point
│       ├── ActionInputs.cs            # Environment variable validation
│       └── Program.cs                 # Main CLI runner
│
├── tests/
│   └── PR_Bot.Core.Tests/             # Comprehensive xUnit test suite
│       ├── Analyzers/                 # Unit tests for all analyzers
│       └── Services/                  # Engine & aggregation tests
│
└── docs/
    ├── USAGE.md                       # Comprehensive setup and parameter guide
    ├── TEST_CASES.md                  # Step-by-step test cases for testing the bot
    ├── INCREMENTAL_REVIEW_APPROACH.md # Delta review & resolution lifecycle
    └── CUSTOM_ANALYZERS.md            # Guide for building custom analyzers
```

---

## 🧪 Running Tests Locally

```bash
# Run all unit tests
dotnet test --verbosity normal

# Build in release configuration
dotnet build -c Release
```

---

## 🤝 Contributing & License

Contributions and custom analyzers are welcome! MIT Licensed.
