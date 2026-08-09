# Set up and run FsHarness

FsHarness can be started manually with `dotnet`, or prepared and operated by Codex. Both modes run the same durable campaign engine.

## Before you start

Install and verify:

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git
- an authenticated Codex CLI compatible with FsHarness's non-interactive commands
- a committed, non-bare Git repository to optimize
- a trusted evaluator that implements the [FsHarness evaluator protocol](../README.md#evaluator-protocol)

```powershell
dotnet --version
git --version
```

The `dotnet` version must begin with `10.`. Your campaign also needs:

- a precise objective;
- repository-relative editable paths;
- a primary metric, its direction, and optionally a target;
- evaluator constraints such as build and tests;
- limits for experiments, tokens, duration, and consecutive failures.

On Windows, use a short data root outside the source checkout, such as `E:\fsh\r12`. Long paths below the repository can prevent native ONNX Runtime provider DLLs from loading.

## Mode 1: run manually with `dotnet`

Clone and restore FsHarness:

```powershell
git clone https://github.com/fwaris/FsHarness.git
Set-Location FsHarness
dotnet restore FsHarness.slnx
```

### Verify the Codex CLI

FsHarness can discover Codex automatically, but an explicit, verified path makes a campaign more reproducible. On Windows, prefer the newest CLI bundled with VS Code or VS Code Insiders, for example:

```powershell
$codex = "C:\Users\<you>\.vscode\extensions\openai.chatgpt-<version>\bin\windows-x86_64\codex.exe"
& $codex --version
$env:FSHARNESS_CODEX_PATH = $codex
```

Do not use a Codex executable below `C:\Program Files\WindowsApps\OpenAI.Codex...`; that is a desktop-app payload, not a reliable headless CLI.

### Create the campaign in the desktop app

Start the campaign editor:

```powershell
dotnet run --project src/FsHarness.App/FsHarness.App.fsproj
```

On the **Setup** page:

1. Select the source repository and enter the objective.
2. Restrict the experiment to the intended editable paths.
3. Configure the evaluator executable, arguments, working directory, required constraints, timeout, and retry policy.
4. Set the primary metric, direction, minimum improvement, and optional target.
5. Choose the model, reasoning effort, graph-search policy, promotion mode, and run budgets.
6. Set a durable data root. On Windows, keep it short and outside the source repository.
7. Select **Inspect repository**, then run the Codex preflight.
8. Save the configuration as `experiment.json` and launch the campaign.

The desktop application edits and monitors the campaign. It launches a separate `FsHarness.Cli` process to import the baseline into private Git storage, run experiments, evaluate candidates, and persist recovery state.

### Run the saved campaign headlessly

Once `experiment.json` exists, you can bypass the desktop application:

```powershell
dotnet run --project src/FsHarness.Cli/FsHarness.Cli.fsproj -- run `
  --config C:\campaigns\experiment.json `
  --data-root E:\fsh\r12 `
  --codex $codex
```

The campaign prints and persists its run ID. Use that ID to inspect or recover it:

```powershell
dotnet run --project src/FsHarness.Cli/FsHarness.Cli.fsproj -- status `
  --run-id <guid> --data-root E:\fsh\r12

dotnet run --project src/FsHarness.Cli/FsHarness.Cli.fsproj -- health `
  --run-id <guid> --data-root E:\fsh\r12

dotnet run --project src/FsHarness.Cli/FsHarness.Cli.fsproj -- resume `
  --run-id <guid> --data-root E:\fsh\r12 --codex $codex
```

Other inspection commands include `leaves`, `children`, `lineage`, `diff`, `plans`, `knowledge`, and `annotations`.

## Mode 2: ask Codex to create and run the campaign

First install the .NET 10 SDK and Git, then clone FsHarness:

```powershell
git clone https://github.com/fwaris/FsHarness.git
```

Open the cloned FsHarness repository in Codex. If the source repository you want to optimize is elsewhere, give Codex its absolute path and make sure it is available in the same accessible workspace.

Then adapt and submit this prompt:

```text
Read README.md and docs/setup-and-run.md completely.

Create and run an FsHarness campaign with this contract:
- Source repository: <absolute path>
- Objective: <one measurable engineering objective>
- Editable paths: <repository-relative paths>
- Primary metric: <name>
- Direction: <maximize or minimize>
- Required constraints: <for example, build and tests>
- Evaluator: <existing command, or create and validate a deterministic evaluator>
- Metric target or minimum delta: <value>
- Maximum experiments: <count>
- Maximum raw tokens: <count>
- Maximum duration: <duration>

Before launching:
1. Verify that the active .NET SDK is version 10.
2. Resolve the newest usable Codex CLI bundled with VS Code or VS Code Insiders,
   run its full path with --version, and pass that path to FsHarness.
3. Inspect the source repository and pin its committed HEAD.
4. Validate the evaluator protocol and baseline result.
5. Save a schema-v4 experiment.json with a short data root outside the source
   checkout; use E:\fsh\r12 on Windows unless that path is unavailable.
6. Do not broaden editable paths or change the source repository outside the
   explicitly approved campaign setup.

Launch the headless campaign, record its run ID and launch command, monitor status
and health, resume preserved state after recoverable interruptions, and finish by
reporting the champion commit, metric, token usage, evaluator evidence, and stop
reason. Stop for my approval before making a Git commit or changing an existing
evaluator contract.
```

For a long campaign, make the prompt a Codex `/goal`. Official OpenAI documentation describes `/goal` as a durable objective that can continue across turns until a verifiable stopping condition is reached:

```text
/goal Create and run the FsHarness campaign using the contract below. Continue
until its metric target or configured budget stops the campaign, then report the
champion and evidence.

<paste the campaign contract from above>
```

If `/goal` is unavailable, enable it with `codex features enable goals`, restart the Codex session if needed, and submit the goal again. See OpenAI's [Follow a goal](https://learn.chatgpt.com/use-cases/follow-goals) guide.

## Two loops, separate responsibilities

When Codex operates FsHarness, the outer Codex session manages setup, launch, monitoring, recovery, and the final report. FsHarness manages the inner experiment graph.

FsHarness deliberately disables goals, apps, hooks, subagents, remote plugins, user configuration, and MCP servers inside experiment workers. Each worker receives one bounded engineering task in an isolated private worktree. This prevents the meta-level campaign controller from leaking into candidate generation.

For configuration fields, evaluator details, storage guarantees, and every CLI command, see the main [FsHarness README](../README.md).
