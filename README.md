# CiAgent

An agent that watches your GitHub Actions CI/CD runs. When one fails, it analyzes
the log and posts the root cause and a suggested fix as a comment on the PR (or
commit). If asked, it can try the fix itself and verify it before applying anything.

## What it does

**Analysis** — When a watched workflow finishes with `failure`:
1. Downloads the logs and annotations of the failed jobs.
2. Parses the failure, locating the `file:line` and the relevant code snippet when possible.
3. Asks Azure OpenAI for the root cause and a suggested fix.
4. Posts the result as a Markdown comment on the PR (or the commit, if there's no open PR).

**`/fix`** — When someone with write access comments `/fix` on the PR:
1. Reuses the existing analysis result when available, instead of asking the model again.
2. Produces the change as a find-and-replace edit and runs it through a safety policy.
3. Applies it, builds, and tests; on failure it reverts and retries with a different approach.
4. Plain `/fix` only shows the result as a suggestion; `/fix --commit` pushes a successful fix to the PR branch.

Analysis is **language-agnostic** (it works off the raw log). `/fix` currently
supports three ecosystems: **.NET**, **Python**, and **Node.js**.

## How it works

```
GitHub ──webhook (workflow_run / issue_comment)──▶ CiAgent.Service
                                                        │
                                    verify signature → enqueue → 202
                                                        │
                                          AnalysisWorker (background)
                                                        │
                                          CiAnalysisPipeline (Core)
                                                        │
                                read logs/annotations → ask the LLM → comment on the PR
                                                        │
                                    (for /fix) a separate Container Apps Job
                                    clones the repo, fixes, verifies, and pushes
```

Authentication follows a "no stored secret" rule on both sides: GitHub is reached
with a GitHub App's short-lived installation token, and Azure OpenAI is reached
with managed identity where possible.

## Project layout

| Project | Role |
|---|---|
| `CiAgent.Core` | All analysis and `/fix` logic (`Analysis/`, `Llm/`, `GitHub/`, `Fix/`, `Common/`) |
| `CiAgent.Cli` | Command-line adapter — for running in a CI job or locally |
| `CiAgent.Service` | Webhook receiver (`Webhook/`) + background worker (`Worker/`), runs on Azure Container Apps |
| `CiAgent.Tests` | xUnit + Moq, ~500 tests |

The logic in `Core` is called the **same way** by both `Cli` and `Service` — the
trigger (command line vs. webhook) changes, but analysis and `/fix` behavior are
owned by one place.

## Running locally

```bash
dotnet build CiAgent.sln
dotnet test CiAgent.sln
```

Try the CLI (analysis):

```bash
dotnet run --project CiAgent.Cli -- <owner> <repo> <runId> --dry-run
```

`--dry-run` runs the analysis (including the Azure OpenAI call) but writes nothing to GitHub.

## Configuration

| Variable | Used by | Description |
|---|---|---|
| `GITHUB_TOKEN` | Cli | GitHub API access (falls back to App auth if unset) |
| `GITHUB_APP_ID`, `GITHUB_APP_PRIVATE_KEY` | Cli, Service | GitHub App identity |
| `GITHUB_WEBHOOK_SECRET` | Service | HMAC verification for incoming webhooks |
| `CI_AGENT_INSTALLATION_ID` | Cli | Target installation when minting its own token |
| `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_DEPLOYMENT` | Cli, Service | Azure OpenAI target |
| `AZURE_OPENAI_KEY` | Cli, Service | Optional — falls back to managed identity |
| `AZURE_CLIENT_ID` | Cli, Service | Selects a user-assigned managed identity |
| `CI_AGENT_MODE` | Cli | `analyze` (default) / `fix` |
| `CI_AGENT_WATCHED_WORKFLOWS` | Service | Workflow names to watch (default `CI,CD`) |
| `CI_AGENT_MAX_JOBS_PER_HOUR` | Service | Rate limit per installation (default 20) |
| `CI_AGENT_ANALYZE_CANCELLED` | Service | Also analyze runs that ended `cancelled` |
| `CI_AGENT_DRY_RUN` | Cli | Same effect as `--dry-run` |

## Deployment

A single Docker image runs in one of two roles depending on `CI_AGENT_MODE`: an
always-on webhook service (Azure Container App) or a one-shot `/fix` job (Azure
Container Apps Job). Deployment happens through
`.github/workflows/release.yml` when a `v*` tag is pushed, authenticating to Azure
via OIDC (no stored secrets) — an ordinary push to `main` does not trigger a deploy.

See [`docs/`](docs) for more detail.

## Security principles

- `/fix` never writes under `.github/workflows/`, to test files, or outside the repo.
- A model-produced edit is rejected unless it matches the target file **exactly and uniquely**.
- A change that doesn't pass build/test verification is never committed.
- Secrets (tokens, passwords, emails) in logs and prompts are masked before they reach the model.
- `/fix` can only be run by the repo owner, an org member, or an invited collaborator.
- `/fix` refuses PRs from forks outright — the agent's token can't push to a fork.

## Limits

- The job queue is in-memory only; a sudden crash can silently drop a pending item (recoverable via a manual "Redeliver").
- If CI is green but the app breaks in production (a coverage gap), there's no failure signal for the agent to react to.
- `/fix` doesn't engage outside .NET/Python/Node.js; analysis still runs regardless of language.
