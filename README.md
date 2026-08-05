# FsHarness

FsHarness is a .NET 10 F# system for durable, graph-search Codex experiment loops. The headless CLI keeps a global champion plus a bounded beam of independently expandable heads. Candidates derive from a selected head but are evaluated against the champion; valid non-winners remain searchable. Eligible incomparable heads can be synthesized into real two-parent Git commits. The desktop application is only a campaign editor and durable-state monitor: it launches and stops a separate CLI process but never hosts campaign execution.

The core and infrastructure layers also expose typed dependency plans and bounded multi-agent execution for custom orchestration. Independent ready work items run through throttled `AsyncSeq` workers; results are ordered deterministically, checked against token/tool budgets, and aggregated with changed-path conflict detection.

The application never initializes, commits, checks out, merges, pushes, or otherwise changes the selected source repository. A dirty source worktree is allowed, but its uncommitted state is excluded from the pinned baseline.

## Run the app

Prerequisites:

- .NET SDK 10
- Git
- a committed, non-bare Git worktree without submodules, sparse checkout, or LFS-dependent mutable content
- a Codex CLI supporting the configured non-interactive flags and model catalog commands
- a trusted evaluator executable implementing the protocol below

```bash
dotnet restore FsHarness.slnx
dotnet run --project src/FsHarness.App/FsHarness.App.fsproj
```

Codex discovery checks `FSHARNESS_CODEX_PATH` first, then `PATH`, then the newest installed OpenAI Codex extension under `~/.vscode/extensions` or `~/.vscode-insiders/extensions`. This lets the macOS desktop app use the CLI bundled with VS Code even when it starts with a reduced GUI `PATH`. Set `FSHARNESS_CLI_PATH` when the desktop app cannot discover the headless FsHarness CLI executable or DLL. Set `FSHARNESS_DATA_DIR` to override the platform-local application data directory.

Setup performs source inspection and optional Codex preflight checks for the campaign editor. Launch starts `FsHarness.Cli` as a separate process; private Git import, baseline evaluation, generation, and candidate evaluation all run there. Stop uses a file-based control request that the CLI converts to `StopNow`, including during preparation. The default model request is `gpt-5.6-luna` with reasoning effort `max`; unsupported model/effort pairs fail closed.

Setup can load and save schema-v4 experiment JSON through the native file picker. Loading restores the complete campaign configuration, including graph-search/synthesis policy, paired comparison, seed patches, evaluator timeout/retries, prompt limits, reasoning effort, and run budgets; the repository must then be inspected again so the app pins its current committed HEAD. An optional top-level `dataRoot` links the experiment to its durable history, work DAG, repository knowledge graph, private Git data, and artifacts. The desktop app adopts that root when the Setup value has not been overridden. The CLI uses precedence `--data-root`, `FSHARNESS_DATA_DIR`, experiment `dataRoot`, then the platform default. Saving requires a successful repository inspection because `baseCommit` is part of the reproducible experiment file. The History page polls durable events, shows per-experiment token consumption, and keeps newest entries at the top. Work-DAG and knowledge-graph data refresh only when requested manually.

## Headless and operational CLI

The CLI can start or recover campaigns and inspect all durable orchestration surfaces:

```bash
fsharness run --config experiment.json
fsharness resume --run-id <guid>
fsharness status [--run-id <guid>]

fsharness children --run-id <guid> --commit <oid>
fsharness leaves --run-id <guid>
fsharness lineage --run-id <guid> --commit <oid>
fsharness diff --run-id <guid> --from <oid> --to <oid>

fsharness plans --run-id <guid>
fsharness knowledge --run-id <guid> --query "scheduler budget"
fsharness annotate --run-id <guid> --author "name" --message "review note"
fsharness annotations --run-id <guid>
fsharness health --run-id <guid>
```

Use `--data-root <dir>` when the run is outside the platform-default data directory. On Windows, use a short root outside the source checkout—such as `E:\fsh\r12`—and keep the run folder name short. Do not place private worktrees below `<repository>\.fsharness\runs-*`: native ONNX Runtime provider DLLs can otherwise exceed the Windows path limit (Error 206). The desktop Setup page also shows the data root for the next run and lets you edit it or choose a folder; the selected root takes effect when you prepare the private run and cannot replace an active or prepared run. `FSHARNESS_DATA_DIR` can set the initial desktop root. `resume` verifies the source and Codex preflight, reconciles pending promotion and evaluator leases, checks artifact hashes, restores the champion, active heads, usage, and attempt counters, and refuses to exceed an already-consumed budget.

## Evaluator protocol

The evaluator is launched directly, without a shell and without inherited secret environment variables. Its working directory is relative to the clean candidate worktree. `FSHARNESS_PARENT_PATH` identifies the derivation parent, `FSHARNESS_CHAMPION_PATH` identifies the comparison target, and `FSHARNESS_FRONTIER_PATH` remains a compatibility alias for the champion. `FSHARNESS_RESULT_PATH` is the only result destination. Exit zero means evaluation completed, even if quality constraints failed. The evaluator must atomically write:

```json
{
  "schemaVersion": 1,
  "constraints": { "build": true, "tests": true },
  "metrics": { "primary": 12.34 },
  "summary": "Human-readable result",
  "evidence": ["Concise diagnostic"]
}
```

The executable and argument list are passed with `ProcessStartInfo.ArgumentList`; shell interpolation is never used. A repository-local evaluator should live below `.fsharness/`, which is always protected from candidate changes.

Evaluator infrastructure failures are distinct from evaluation outcomes. Process start failures, nonzero exits (including SSH disconnects), timeouts, and missing result files retry the same preserved candidate after `evaluator.infrastructureRetryDelaySeconds`. `evaluator.maxInfrastructureRetries` bounds the outage window; new and legacy campaigns default to 120 retries at 15-second intervals (about 30 minutes). Each scheduled retry is journaled as `EvaluatorRetryScheduled`, remains responsive to campaign stop requests, and does not consume another experiment or Codex tokens. Invalid result JSON and configuration errors remain terminal because retrying them cannot repair the evaluator protocol.

## Storage and safety model

- Source Git is inspected read-only with optional locks disabled.
- Each run is imported using `git clone --bare --no-local --no-hardlinks`, then stripped of remotes, alternates, hooks, and automatic pruning.
- A stable OS-level lock permits only one prepared run for a source repository.
- Generation, assembly, and evaluation use separate app-owned worktrees.
- Candidate refs are retained under `refs/fsharness/runs/<run>/candidates/...`; baseline and champion compatibility refs are retained separately.
- Champion promotion is compare-and-swap and happens only after an `AcceptPending` journal event. A synthesis commit is written with both real Git parents.
- Promotion and evaluator execution use durable intents. Recovery resumes the preserved candidate and reuses a completed evaluator result before issuing duplicate GPU work.
- `.git`, `.fsharness`, `.gitmodules`, `.gitattributes`, gitlinks, and changed symlinks/reparse points are protected regardless of the editable allowlist.
- Codex workers are writable by default through `--dangerously-bypass-approvals-and-sandbox`, but run from private generation worktrees and are still constrained by the editable-path snapshot validator. Set `FSHARNESS_CODEX_WRITE_POLICY=workspace-write` or `read-only` to opt into a stricter worker policy.
- Unrestricted mode removes Codex's OS sandbox. It must only be used with FsHarness's isolated worktree workflow; prompt restrictions and post-generation protected-path validation remain active, but unrestricted mode is not a substitute for OS-level containment.
- Immediate stop kills the complete process tree, attempts to preserve allowed partial changes, and cannot promote them.
- Prompts contain the objective, selected parent, current champion, evaluator feedback, and a bounded two-hop repository-graph context with stable edge citations, contradictions, and uncertainty. Previous transcripts and private reasoning are never injected.
- Raw JSONL, stderr, prompts, diffs/evaluator results, SQLite records, and private Git lineage remain in app-owned storage.
- SQLite schema-v6 migrations create a pre-v6 backup, run transactionally, and persist work edges, champion history, active heads, scheduler rounds, synthesis attempts, idempotent repository knowledge updates, work plans, durable operations, reproducibility manifests, and annotations.

Missing terminal token usage is not interpreted as zero: the active candidate is evaluated and then the run pauses because the remaining budget cannot be enforced. Raw and uncached totals use the Codex counters without double-counting cached input or reasoning output.

## Evolution view

The **Evolution** page lays commits out by topological depth and draws every persisted parent edge. Synthesis nodes appear as diamonds, the champion and active heads are labeled, and selecting a node highlights its ancestors and descendants. Rejected constraint-valid candidates remain visible and expandable; failed, protected, inconclusive, and constraint-failing candidates remain durable but inactive. Seed patches appear before Codex attempts.

The metric view plots the selected primary lineage while retaining the global champion envelope; unrelated branches are not connected by sequence. Selecting a node shows its outcome, commit, metric, timestamp, and any persisted experiment summary. The graph remains unchanged until **Refresh** is selected.

The separate **Knowledge** page shows repository-scoped, append-only entities, claims, sources, artifacts, agent runs, evaluations, tasks, commits, metrics, and typed relations with stable IDs and provenance. Older runs are migrated in place: single-parent work edges, champion/head state, and legacy claims are backfilled without fabricating synthesis links.

## Hypothesis benchmark

The frozen benchmark contract and scorer are in `HypothesisBenchmark`. It defines four task categories and three arms:

- `gpt-5.6-sol` / Medium / one turn
- `gpt-5.6-luna` / Max / one turn
- `gpt-5.6-luna` / Max / external ratchet, at most three candidates

Each episode has a nominal 50,000-token and 15-minute cap. The UI discloses the 600,000-token aggregate cap plus possible active-turn overshoot. Live account-consuming execution is never automatic. The pure scorer emits only `Promising`, `NotYetPromising`, or `Inconclusive`; missing usage is always inconclusive and failed episodes are charged the nominal cap.

This smoke protocol does not establish statistical significance, causality, generalization, a Max-specific effect, or an FsColBERT benefit.

## Memory

Generation uses bounded repository-graph retrieval rather than flat recent-memory injection. The legacy functional `MemoryPort` remains available for compatibility and reporting. FsColBERT is intentionally not on the critical path; the adapters leave room for a later retrieval backend after an equal-context A/B test demonstrates better quality or lower tokens-to-quality.

## Verify

```bash
dotnet tool restore
dotnet fantomas --check .
dotnet build FsHarness.slnx -c Release --no-restore
dotnet test tests/FsHarness.Tests/FsHarness.Tests.fsproj -c Release --no-build --no-restore
```

Tests cover reducer/comparator/token rules, DAG acyclicity and traversal, deterministic beam selection, synthesis cadence/deduplication, true two-parent Git commits, champion/parent evaluator separation, evaluator retries, provenance validation and two-hop retrieval, schema-v6 migration/backup, graph isolation/idempotency, artifact integrity, project locking, real fake-process adapters, and Avalonia.Headless diamond rendering.

## Prepare a transport copy

Remove generated `bin` and `obj` directories before copying the repository. The script stays within this checkout, always skips `.git` and reparse points, and supports PowerShell's standard preview mode:

```powershell
./Clean-BuildArtifacts.ps1 -WhatIf
./Clean-BuildArtifacts.ps1
```

For a source-only transport copy, opt in to removing the repository-root `.fsharness` directory as well. This permanently removes its local evaluator files and campaign data, so preview the exact targets first:

```powershell
./Clean-BuildArtifacts.ps1 -IncludeFsHarness -WhatIf
./Clean-BuildArtifacts.ps1 -IncludeFsHarness
```

The paid live Codex benchmark is deliberately opt-in and is not part of automated tests.
