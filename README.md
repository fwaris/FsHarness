# FsHarness

FsHarness is a .NET 10 Avalonia FuncUI desktop application that runs a single-worker Codex experiment ratchet. Each worker starts from the retained best commit, receives a bounded prompt, produces an immutable candidate, and is evaluated in a separate clean worktree. Only a constraint-passing strict metric improvement can advance the private frontier.

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

Codex discovery checks `FSHARNESS_CODEX_PATH` first, then `PATH`, then the newest installed OpenAI Codex extension under `~/.vscode/extensions` or `~/.vscode-insiders/extensions`. This lets the macOS desktop app use the CLI bundled with VS Code even when it starts with a reduced GUI `PATH`. Set `FSHARNESS_DATA_DIR` to override the platform-local application data directory.

Setup performs source inspection, `codex --version`, `codex login status`, `codex doctor --json`, bundled model discovery, private Git import, and baseline evaluation before Start is enabled. The default model request is `gpt-5.6-luna` with reasoning effort `max`; unsupported model/effort pairs fail closed.

Setup can load and save schema-v2 experiment JSON through the native file picker. Loading restores the complete campaign configuration, including paired comparison, seed patches, evaluator timeout/retries, prompt limits, reasoning effort, and run budgets; the repository must then be inspected again so the app pins its current committed HEAD. Saving requires a successful repository inspection because `baseCommit` is part of the reproducible experiment file. Files saved by the app can be passed directly to the headless `fsharness run --config` command.

## Evaluator protocol

The evaluator is launched directly, without a shell and without inherited secret environment variables. Its working directory is relative to the clean candidate worktree. `FSHARNESS_RESULT_PATH` is the only result destination. Exit zero means evaluation completed, even if quality constraints failed. The evaluator must atomically write:

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

## Storage and safety model

- Source Git is inspected read-only with optional locks disabled.
- Each run is imported using `git clone --bare --no-local --no-hardlinks`, then stripped of remotes, alternates, hooks, and automatic pruning.
- A stable OS-level lock permits only one prepared run for a source repository.
- Generation, assembly, and evaluation use separate app-owned worktrees.
- Candidate refs are retained under `refs/fsharness/runs/<run>/candidates/...`; baseline and frontier have dedicated refs.
- Frontier promotion is compare-and-swap and happens only after an `AcceptPending` journal event.
- `.git`, `.fsharness`, `.gitmodules`, `.gitattributes`, gitlinks, and changed symlinks/reparse points are protected regardless of the editable allowlist.
- Immediate stop kills the complete process tree, attempts to preserve allowed partial changes, and cannot promote them.
- Prompts contain the objective, policy, frontier score, evaluator feedback, and at most five distilled memories totaling 6,000 characters. Previous transcripts and private reasoning are never injected.
- Raw JSONL, stderr, prompts, diffs/evaluator results, SQLite records, and private Git lineage remain in app-owned storage.

Missing terminal token usage is not interpreted as zero: the active candidate is evaluated and then the run pauses because the remaining budget cannot be enforced. Raw and uncached totals use the Codex counters without double-counting cached input or reasoning output.

## Evolution view

The **Evolution** page lets you select any persisted run and inspect its retained frontier over time. The lineage view places the baseline and accepted candidates on the central trunk; rejected, failed, inconclusive, and cancelled candidates remain visible as terminal side branches. Seed patches appear before Codex attempts.

The metric view uses the configured metric's raw values and overlays the retained-score line, which advances only when a candidate is accepted. Selecting a node shows its outcome, commit, metric, timestamp, and any persisted experiment summary. New runs populate the SQLite experiment projection as they execute and refresh the page live through the runtime lineage event.

Older runs are reconstructed best-effort from their journal events, evaluations, memories, and private Git refs. If an older run is missing metadata or its private repository, the page shows the available partial lineage and a warning rather than changing the stored run. The visualization describes FsHarness's existing single-frontier ratchet; it does not schedule independent branches.

## Hypothesis benchmark

The frozen benchmark contract and scorer are in `HypothesisBenchmark`. It defines four task categories and three arms:

- `gpt-5.6-sol` / Medium / one turn
- `gpt-5.6-luna` / Max / one turn
- `gpt-5.6-luna` / Max / external ratchet, at most three candidates

Each episode has a nominal 50,000-token and 15-minute cap. The UI discloses the 600,000-token aggregate cap plus possible active-turn overshoot. Live account-consuming execution is never automatic. The pure scorer emits only `Promising`, `NotYetPromising`, or `Inconclusive`; missing usage is always inconclusive and failed episodes are charged the nominal cap.

This smoke protocol does not establish statistical significance, causality, generalization, a Max-specific effect, or an FsColBERT benefit.

## Memory

V1 uses bounded SQLite recency/lineage retrieval through the functional `MemoryPort`. FsColBERT is intentionally not on the critical path: the small append-only experiment history does not yet justify a rebuild-oriented vector index. The port keeps an adapter possible after an equal-context A/B test demonstrates fewer duplicate attempts or lower tokens-to-quality without lower quality.

## Verify

```bash
dotnet tool restore
dotnet fantomas --check .
dotnet build FsHarness.slnx -c Release --no-restore
dotnet test tests/FsHarness.Tests/FsHarness.Tests.fsproj -c Release --no-build --no-restore
```

Tests cover the pure reducer/comparator/token rules, JSONL and output schemas, real fake-process adapters, SQLite journaling/memory, source-preserving private Git candidate capture and frontier CAS, project locking, and an Avalonia.Headless render/keyboard smoke at 1024×680 and 1440×900.

The paid live Codex benchmark is deliberately opt-in and is not part of automated tests.
