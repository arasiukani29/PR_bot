# 🧪 Complete Test Cases Guide for PR Review Bot

A practical, step-by-step guide to test and verify every capability of the **Universal GitHub PR Review Bot**.

All test cases are written in **simple, easy-to-understand words** with exact code snippets, git commands, and expected outcomes.

---

## 📑 Table of Contents

1. [Prerequisites & Setup](#1-prerequisites--setup)
2. [Quick Test Matrix (Summary)](#2-quick-test-matrix-summary)
3. [Core Workflow Test Cases (Step-by-Step)](#3-core-workflow-test-cases-step-by-step)
   - [TC-01: Baseline Clean PR (No Bugs)](#tc-01-baseline-clean-pr-no-bugs)
   - [TC-02: Bug Detection (Single Critical Bug)](#tc-02-bug-detection-single-critical-bug)
   - [TC-03: Bug Resolution & Incremental Push (Resolve & Verify)](#tc-03-bug-resolution--incremental-push-resolve--verify)
   - [TC-04: Incomplete / Flawed Fix (Bot Catches Remaining Bug)](#tc-04-incomplete--flawed-fix-bot-catches-remaining-bug)
   - [TC-05: Multi-File PR (Zero-Skipping Check across 2+ Files)](#tc-05-multi-file-pr-zero-skipping-check-across-2-files)
   - [TC-06: Severity Tiers & Health Score Calculation Check](#tc-06-severity-tiers--health-score-calculation-check)
   - [TC-07: Dual Comments & One-Click Suggestion Blocks](#tc-07-dual-comments--one-click-suggestion-blocks)
   - [TC-08: Untouched File Handling (Delta Review Scope)](#tc-08-untouched-file-handling-delta-review-scope)
4. [Configuration & Edge Case Test Cases](#4-configuration--edge-case-test-cases)
   - [TC-09: Fail-On-Errors Flag (`fail-on-errors: true`)](#tc-09-fail-on-errors-flag-fail-on-errors-true)
   - [TC-10: Non-Code & Ignored Files (`README.md`, docs, images)](#tc-10-non-code--ignored-files-readmemd-docs-images)
   - [TC-11: Pull Request Reopened Event (`reopened`)](#tc-11-pull-request-reopened-event-reopened)
   - [TC-12: Missing Gemini API Key (Fallback Mode)](#tc-12-missing-gemini-api-key-fallback-mode)
   - [TC-13: Invalid Gemini API Key or Quota Handling](#tc-13-invalid-gemini-api-key-or-quota-handling)
5. [Local Simulation (Test Locally Without Pushing to GitHub)](#5-local-simulation-test-locally-without-pushing-to-github)

---

## 1. Prerequisites & Setup

Before running tests on GitHub:

1. **Workflow Installed:** Make sure `.github/workflows/pr-review.yml` exists in your repo:
   ```yaml
   name: PR Review Bot

   on:
     pull_request:
       types: [opened, synchronize, reopened]

   permissions:
     contents: read
     pull-requests: write
     issues: write

   jobs:
     review:
       runs-on: ubuntu-latest
       steps:
         - name: Checkout Repository
           uses: actions/checkout@v4

         - name: Run PR Bot
           uses: ./ # Or YOUR_USERNAME/PR_Bot@main
           with:
             github-token: ${{ secrets.GITHUB_TOKEN }}
             gemini-api-key: ${{ secrets.GEMINI_API_KEY }}
             fail-on-errors: 'false'
   ```
2. **Gemini API Key Secret:**
   - In GitHub: **Settings** → **Secrets and variables** → **Actions** → **New repository secret**.
   - Name: `GEMINI_API_KEY`
   - Value: *(Your key from Google AI Studio)*

---

## 2. Quick Test Matrix (Summary)

| Test ID | Test Scenario | Trigger Event | Input Code | Expected Health Score | Expected Bot Action |
|:---|:---|:---:|:---|:---:|:---|
| **TC-01** | Clean PR (No Bugs) | `opened` | Clean helper class | **100/100 (Grade A)** | ✅ Summary comment saying clean PR; 0 inline comments. |
| **TC-02** | Detect Critical Bug | `opened` | SQL injection or hardcoded secret | **70–75/100 (Grade B/C)** | 🔴 Flags issue, drops score, adds inline comment with fix. |
| **TC-03** | Resolve & Push (User Example) | `synchronize` | Fix bug and push commit | **100/100 (Grade A)** | ✨ Old comment becomes `[Outdated]`; score updates to 100%. |
| **TC-04** | Incomplete Fix | `synchronize` | Partial fix or new bug | **< 100/100** | ⚠️ Re-flags remaining issue or reports new issue. |
| **TC-05** | Multi-File Changes | `opened` | 2+ files with distinct bugs | Score reflects all bugs | 📋 Bot reviews all files without skipping file 2 or 3. |
| **TC-06** | Severity Score Check | `opened` | Critical, High, Medium, Low bugs | Exact mathematical deductions | -30 (Crit), -25 (High), -10 (Med), -3 (Low). |
| **TC-07** | One-Click Suggestions | `opened` | Syntax or logic flaw | Any | Inline comment has GitHub ````suggestion` box. |
| **TC-08** | Untouched File Scope | `synchronize` | Fix File A, leave File B untouched | Partial score recovery | File A comment outdated; File B comment stays active. |
| **TC-09** | Enforce Merge Failure | `opened` | Critical bug with `fail-on-errors: true` | Failing score | ❌ GitHub Action check turns Red (Job Fails). |
| **TC-10** | Non-Code Changes | `opened` | Only `README.md` modified | N/A | ℹ️ Bot gracefully skips; no useless comments or token waste. |
| **TC-11** | Reopened PR | `reopened` | Reopen an existing PR | Same as last commit | 🔄 Bot re-triggers and updates the review summary. |
| **TC-12** | Missing API Key | `opened` | Empty `GEMINI_API_KEY` | 100/100 (Rule-based) | ⚠️ Warns in logs; skips AI review without crashing. |
| **TC-13** | Invalid API Key | `opened` | Corrupted `GEMINI_API_KEY` | N/A | ❌ Bot fails with error 1; does not post false approval. |

---

## 3. Core Workflow Test Cases (Step-by-Step)

### TC-01: Baseline Clean PR (No Bugs)

**Goal:** Verify that the bot does not generate false alarms on clean, correct code.

#### Steps:
1. Create a new branch:
   ```bash
   git checkout -b test/clean-pr
   ```
2. Add a clean C# file `src/MathHelper.cs`:
   ```csharp
   namespace PR_Bot.Samples;

   public static class MathHelper
   {
       public static int Add(int a, int b)
       {
           return a + b;
       }

       public static bool IsEven(int number)
       {
           return number % 2 == 0;
       }
   }
   ```
3. Commit and push:
   ```bash
   git add src/MathHelper.cs
   git commit -m "feat: add clean MathHelper class"
   git push origin test/clean-pr
   ```
4. Open a Pull Request from `test/clean-pr` into `main`.

#### Expected Result:
- [x] Bot completes with exit code `0`.
- [x] The bot posts a sticky summary on the PR conversation tab:
  - **Code Health Score:** `100/100 — Grade: A (Excellent)`
  - Progress bar: `[████████████████████] 100%`
  - All issue counts: `0 Critical, 0 High, 0 Medium, 0 Low`.
  - Message: `✨ No code quality or security issues detected! Clean PR. 🎉`
- [x] **Zero inline comments** created on the "Files changed" tab.
- [x] Workflow run turns **Green ✅**.

---

### TC-02: Bug Detection (Single Critical Bug)

**Goal:** Verify that the bot identifies a critical security/logic bug and provides a clear explanation with suggested fix.

#### Steps:
1. Create a new branch:
   ```bash
   git checkout -b test/security-bug
   ```
2. Add a file with a SQL injection flaw `src/UserRepository.cs`:
   ```csharp
   using System.Data.SqlClient;

   namespace PR_Bot.Samples;

   public class UserRepository
   {
       private readonly string _connectionString = "Server=myServerAddress;Database=myDataBase;";

       public void DeleteUser(string username)
       {
           using var connection = new SqlConnection(_connectionString);
           connection.Open();
           // VULNERABLE: Direct string interpolation leads to SQL Injection!
           var query = $"DELETE FROM Users WHERE Username = '{username}'";
           using var command = new SqlCommand(query, connection);
           command.ExecuteNonQuery();
       }
   }
   ```
3. Commit and push:
   ```bash
   git add src/UserRepository.cs
   git commit -m "feat: add DeleteUser endpoint"
   git push origin test/security-bug
   ```
4. Open a Pull Request on GitHub.

#### Expected Result:
- [x] The bot runs and finds the bug.
- [x] Top summary comment shows:
  - **Code Health Score:** Dropped to **`70–75/100`** (Grade: B or C).
  - Issue table: `🚨 Critical: 1` or `🔴 High Priority: 1`.
- [x] On the **"Files changed"** tab, an **inline comment** appears directly on the `var query = ...` line:
  - Explains the SQL injection vulnerability.
  - Explains the blast radius (attacker could delete arbitrary database tables).
  - Provides a parameterized query code fix.

---

### TC-03: Bug Resolution & Incremental Push (Resolve & Verify)

> 💡 **This is your exact scenario: Add bug → Bot checks → Resolve bug → Push to branch → Bot verifies fix.**

**Goal:** Verify that pushing a fix commit resolves the previous findings, marks old comments `[Outdated]`, and restores the score to 100%.

#### Steps:
1. In the same branch from **TC-02** (`test/security-bug`), open `src/UserRepository.cs`.
2. Fix the SQL injection by using parameterized queries:
   ```csharp
   using System.Data;
   using System.Data.SqlClient;

   namespace PR_Bot.Samples;

   public class UserRepository
   {
       private readonly string _connectionString = "Server=myServerAddress;Database=myDataBase;";

       public void DeleteUser(string username)
       {
           using var connection = new SqlConnection(_connectionString);
           connection.Open();
           // FIXED: Using parameterized query
           var query = "DELETE FROM Users WHERE Username = @Username";
           using var command = new SqlCommand(query, connection);
           command.Parameters.Add("@Username", SqlDbType.VarChar, 50).Value = username;
           command.ExecuteNonQuery();
       }
   }
   ```
3. Commit and push the fix:
   ```bash
   git add src/UserRepository.cs
   git commit -m "fix: parameterize SQL query in DeleteUser to prevent injection"
   git push origin test/security-bug
   ```
4. Watch the GitHub Action automatically trigger on the `synchronize` event.

#### Expected Result:
- [x] **GitHub Conversation Tab:**
  - The previous inline comment thread is now marked with a gray **`[Outdated]`** badge and collapsed automatically by GitHub.
- [x] **Summary Comment:**
  - The sticky summary at the top is **updated** (no duplicate comment created):
  - **Code Health Score:** Rises to **`100/100 — Grade: A (Excellent)`**.
  - All issues cleared to `0`.
  - Message updates to: `✨ No code quality or security issues detected! Clean PR. 🎉`.
- [x] **No new inline comments** appear on the updated commit.
- [x] The PR is now completely clean and ready to merge!

---

### TC-04: Incomplete / Flawed Fix (Bot Catches Remaining Bug)

**Goal:** If a developer attempts a fix but leaves a bug or introduces a new one, verify that the bot does NOT falsely approve the PR.

#### Steps:
1. In `src/UserRepository.cs`, push a flawed fix (for example, attempting to sanitize the string with flawed manual replacement or introducing a `NullReferenceException`):
   ```csharp
   public void DeleteUser(string username)
   {
       // Incomplete fix: Still vulnerable to double-quote or secondary injection, plus throws if username is null!
       string sanitized = username.Replace("'", "''");
       var query = $"DELETE FROM Users WHERE Username = '{sanitized}'";
   }
   ```
2. Commit and push:
   ```bash
   git add src/UserRepository.cs
   git commit -m "fix: attempt string sanitization"
   git push origin test/security-bug
   ```

#### Expected Result:
- [x] The bot re-analyzes the new code.
- [x] The bot flags that manual string replacement is insufficient for SQL injection or flags the null-dereference risk.
- [x] Health Score remains penalized (does not say 100/100).
- [x] New comment points to the updated line with advice to use parameters.

---

### TC-05: Multi-File PR (Zero-Skipping Check across 2+ Files)

**Goal:** Verify that when a PR modifies multiple files, the bot audits **every file** and does not stop after the first file.

#### Steps:
1. Create a branch:
   ```bash
   git checkout -b test/multi-file
   ```
2. Add **File 1** (`src/AuthService.cs`) with a hardcoded secret:
   ```csharp
   namespace PR_Bot.Samples;

   public class AuthService
   {
       // BUG: Hardcoded secret!
       private const string JwtSecret = "super-secret-key-1234567890-hardcoded";

       public string GetToken() => JwtSecret;
   }
   ```
3. Add **File 2** (`src/OrderService.cs`) with an unhandled null crash:
   ```csharp
   namespace PR_Bot.Samples;

   public class OrderService
   {
       public decimal CalculateTotal(OrderRequest request)
       {
           // BUG: request.Items can be null, causing unhandled NullReferenceException!
           return request.Items.Sum(i => i.Price * i.Quantity);
       }
   }

   public class OrderRequest
   {
       public List<OrderItem>? Items { get; set; }
   }

   public class OrderItem
   {
       public decimal Price { get; set; }
       public int Quantity { get; set; }
   }
   ```
4. Commit both files and push:
   ```bash
   git add src/AuthService.cs src/OrderService.cs
   git commit -m "feat: add auth and order services"
   git push origin test/multi-file
   ```
5. Open PR.

#### Expected Result:
- [x] The bot reviews **both** `AuthService.cs` and `OrderService.cs`.
- [x] Inline comment appears on `AuthService.cs` for the hardcoded secret.
- [x] Inline comment appears on `OrderService.cs` for the null reference hazard.
- [x] The summary table lists findings across **both** files under the "Review Findings" section.

---

### TC-06: Severity Tiers & Health Score Calculation Check

**Goal:** Verify that findings are properly classified into the 4 tiers and that deductions match the specification:

| Priority | Deduction | Impact |
|:---|:---:|:---|
| 🚨 **Critical** | -30 pts | Immediate blocker (secrets, SQLi, system crash) |
| 🔴 **High Priority** | -25 pts | Severe defect (major logic flaw, race condition) |
| 🟡 **Medium Priority** | -10 pts | Recommended fix (unvalidated input, inverted logic) |
| 🟢 **Low Priority** | -3 pts | Suggestions (code hygiene, readability) |

#### Steps:
1. Create a PR with 1 High Priority bug (e.g. unhandled null pointer crash).
2. Observe score: $100 - 25 = \mathbf{75/100}$ (Grade: B).
3. Push a commit that adds 1 Medium Priority bug (e.g. missing range validation on integer page size).
4. Observe score: $100 - 25 - 10 = \mathbf{65/100}$ (Grade: C).

#### Expected Result:
- [x] The summary table accurately displays counts for each severity.
- [x] The visual meter updates: `[█████████████░░░░░░░] 65%`.
- [x] The letter grade reflects the score according to boundaries (A: 90+, B: 75+, C: 50+, F: <50).

---

### TC-07: Dual Comments & One-Click Suggestion Blocks

**Goal:** Verify that inline comments provide GitHub-native suggestion blocks that developers can accept with 1 click.

#### Steps:
1. Create a PR where a method has a simple fixable bug (e.g. off-by-one or missing check).
2. Go to the **Files changed** tab in the GitHub PR.

#### Expected Result:
- [x] The inline comment has a formatted suggestion block:
  ```suggestion
  // Suggested replacement code provided by the bot
  ```
- [x] A **"Commit suggestion"** button is displayed by GitHub, allowing the author to apply the fix directly from the browser!

---

### TC-08: Untouched File Handling (Delta Review Scope)

**Goal:** In a multi-file PR, if the developer fixes File 1 but does not touch File 2, File 2's comment should remain active.

#### Steps:
1. Start with the PR from **TC-05** (`AuthService.cs` has a bug, `OrderService.cs` has a bug).
2. Modify and fix **only** `AuthService.cs`.
3. Commit and push:
   ```bash
   git add src/AuthService.cs
   git commit -m "fix: load secret from environment variable"
   git push origin test/multi-file
   ```
4. Observe the PR on GitHub.

#### Expected Result:
- [x] Comment on `AuthService.cs` is marked **`[Outdated]`**.
- [x] Comment on `OrderService.cs` remains **Active (Visible & Unresolved)** because `OrderService.cs` was not touched in this commit.
- [x] Summary Health Score recovers partially (from the fixed file), but still reflects the remaining bug in `OrderService.cs`.

---

## 4. Configuration & Edge Case Test Cases

### TC-09: Fail-On-Errors Flag (`fail-on-errors: true`)

**Goal:** Verify that `fail-on-errors: true` blocks merging by failing the GitHub workflow check when critical errors are found.

#### Steps:
1. Update `.github/workflows/pr-review.yml` in your test branch:
   ```yaml
   with:
     github-token: ${{ secrets.GITHUB_TOKEN }}
     gemini-api-key: ${{ secrets.GEMINI_API_KEY }}
     fail-on-errors: 'true'
   ```
2. Add a critical security bug (e.g. exposed plaintext password).
3. Commit and push.

#### Expected Result:
- [x] Bot detects the critical error.
- [x] The GitHub Actions job exits with code `1`.
- [x] The check mark at the bottom of the PR turns **Red ❌** ("Automated Code Review / review — Failed").
- [x] If branch protection is enabled, GitHub blocks the "Merge pull request" button until fixed.

---

### TC-10: Non-Code & Ignored Files (`README.md`, docs, images)

**Goal:** Verify that non-code edits do not trigger unnecessary AI calls or false comments.

#### Steps:
1. Create a branch:
   ```bash
   git checkout -b test/docs-only
   ```
2. Edit only documentation, such as adding a line to `README.md` or a `.png` image.
3. Commit and push:
   ```bash
   git add README.md
   git commit -m "docs: update readme text"
   git push origin test/docs-only
   ```
4. Open PR.

#### Expected Result:
- [x] Bot checks changed files and detects no reviewable code patches.
- [x] Logs output: `ℹ️ No reviewable file changes detected in this pull request.`
- [x] Bot exits cleanly with code `0`.
- [x] No spam comments posted on the PR.

---

### TC-11: Pull Request Reopened Event (`reopened`)

**Goal:** Verify that closing and reopening a PR triggers the bot cleanly.

#### Steps:
1. Open a PR with an existing review.
2. Click **Close pull request** at the bottom of the PR.
3. Wait 5 seconds, then click **Reopen pull request**.

#### Expected Result:
- [x] GitHub Action triggers with event `reopened`.
- [x] The bot reviews the current HEAD commit and refreshes the summary comment.

---

### TC-12: Missing Gemini API Key (Fallback Mode)

**Goal:** Verify that if `GEMINI_API_KEY` is not provided, the bot does not crash.

#### Steps:
1. In the workflow, temporarily remove or comment out `gemini-api-key`:
   ```yaml
   with:
     github-token: ${{ secrets.GITHUB_TOKEN }}
     # gemini-api-key: ""
   ```
2. Push a commit to trigger the bot.

#### Expected Result:
- [x] Bot logs: `⚠️ No GEMINI_API_KEY detected. Please configure GEMINI_API_KEY to enable AI code review.`
- [x] Bot continues running without crashing.
- [x] Completes with code `0`.

---

### TC-13: Invalid Gemini API Key or Quota Handling

**Goal:** Verify that if the API key is invalid or quota is exceeded, the bot fails gracefully rather than posting a false "All Clear / 100%" approval.

#### Steps:
1. In the workflow, pass an invalid API key:
   ```yaml
   with:
     github-token: ${{ secrets.GITHUB_TOKEN }}
     gemini-api-key: 'INVALID_AI_KEY_TEST_999'
   ```
2. Push a commit with a bug.

#### Expected Result:
- [x] Bot catches the authentication error from Google Gemini API.
- [x] Bot logs: `❌ AI Review failed because model was unavailable or quota was exceeded. Aborting job without posting false approval.`
- [x] Exits with code `1`.
- [x] **Does NOT** post a false "100/100 Clean PR" comment.

---

## 5. Local Simulation (Test Locally Without Pushing to GitHub)

You don't always need to wait for GitHub Actions runners. You can test the engine and analyzer locally in seconds:

### 1. Run Unit Tests (Instant Verification)
```powershell
dotnet test --verbosity normal
```
*Expected: 30 passed tests.*

### 2. Run Local CLI Dry-Run Simulation
You can simulate the GitHub Action console app locally using PowerShell:

```powershell
# Set environment variables for simulation
$env:GITHUB_TOKEN="dummy_token"
$env:GITHUB_REPOSITORY="your-user/your-repo"
$env:PR_NUMBER="1"
$env:PR_TITLE="Test Feature PR"
$env:PR_AUTHOR="tester"
$env:PR_BASE="main"
$env:PR_HEAD="feature"
$env:PR_HEAD_SHA="HEAD"
$env:GEMINI_API_KEY="YOUR_ACTUAL_GEMINI_KEY" # Optional

# Run the console action app
dotnet run --project src/PR_Bot.Action/PR_Bot.Action.csproj
```

---

## 🎯 Verification Checklist for Testers

When testing any Pull Request, use this **5-second checklist**:

| Check Item | Where to Look | Expected State |
|:---|:---|:---|
| **1. Conversation Sticky Review & Status** | PR Conversation tab / Header | Shows `🤖 Universal PR Review Bot` dashboard with color progress bar and proper GitHub Review Event status (`requested changes` on critical/high, `approved` on clean, or `commented`). |
| **2. Inline Comments** | Files Changed tab | Shows inline comment on the exact line with ````suggestion` code snippet. |
| **3. Fix Push (`[Outdated]`)** | PR Conversation tab | Old comments have gray `[Outdated]` tag after pushing fix. |
| **4. Score Update** | PR Conversation tab | Summary score updates to `100/100` after all bugs are resolved. |
| **5. CI Status Badge** | Bottom of PR | Green check `✅ Automated Code Review` (or Red if `fail-on-errors: true` with bugs). |

---

*Documentation maintained for [Universal PR Review Bot](https://github.com/marketplace).*
