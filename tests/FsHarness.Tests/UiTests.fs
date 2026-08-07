namespace FsHarness.Tests

open System
open System.IO
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Elmish
open Avalonia.FuncUI.Hosts
open Avalonia.Headless
open Avalonia.Input
open Avalonia.Threading
open Avalonia.VisualTree
open Elmish
open FsHarness.App
open FsHarness.Core
open FsHarness.Infrastructure
open Microsoft.Data.Sqlite
open Xunit

type HeadlessAppBuilder =
    static member BuildAvaloniaApp() =
        let options = AvaloniaHeadlessPlatformOptions()
        options.UseHeadlessDrawing <- false
        AppBuilder.Configure<App>().UseSkia().UseHeadless(options)

module UiTests =
    let private update runtime message model =
        AppState.update
            runtime
            (fun () -> async { return Ok None })
            (fun () -> async { return Ok None })
            (fun () -> async { return Ok None })
            (fun _ _ -> async { return Ok None })
            "codex"
            message
            model

    [<Fact>]
    let ``font controls clamp the UI scale`` () =
        use runtime = new HarnessRuntime("codex")
        let model, _ = AppState.init runtime

        let enlarged, _ = update runtime IncreaseFont model
        Assert.Equal(1.1, enlarged.FontScale, 5)

        let minimum = { model with FontScale = 0.8 }
        let unchanged, _ = update runtime DecreaseFont minimum
        Assert.Equal(0.8, unchanged.FontScale, 5)

    [<Fact>]
    let ``window title keeps the loaded filename and compacts its directory`` () =
        let fileName = "important-campaign-settings.json"
        let directory = $"/a/{String('b', 80)}/campaigns"
        let title = WindowTitle.forExperiment (Some(Path.Combine(directory, fileName)))

        Assert.Contains(fileName, title)
        Assert.Contains("…", title)
        Assert.DoesNotContain(String('b', 80), title)

    [<Fact>]
    let ``unchanged text notifications preserve the loaded campaign`` () =
        use runtime = new HarnessRuntime("codex")
        let model, _ = AppState.init runtime

        let loaded =
            { model with
                ExperimentFile = Some "/tmp/campaign.json" }

        let updated, _ =
            update runtime (DraftChanged(DraftField.Objective, loaded.Draft.Objective)) loaded

        Assert.Equal(loaded, updated)

    [<Fact>]
    let ``headless campaign launch persists liveness and stop control`` () =
        if not (OperatingSystem.IsWindows()) then
            lock typeof<HeadlessCampaignStatus> (fun () ->
                let directory =
                    Path.Combine(Path.GetTempPath(), $"fsharness-headless-{Guid.NewGuid():N}")

                Directory.CreateDirectory directory |> ignore
                let executable = Path.Combine(directory, "fake-fsharness")
                let configPath = Path.Combine(directory, "campaign.json")
                let dataRoot = Path.Combine(directory, "data")
                File.WriteAllText(executable, "#!/bin/sh\nsleep 1\n")

                File.SetUnixFileMode(
                    executable,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                )

                File.WriteAllText(configPath, "{}")
                let previous = Environment.GetEnvironmentVariable "FSHARNESS_CLI_PATH"

                try
                    Environment.SetEnvironmentVariable("FSHARNESS_CLI_PATH", executable)

                    let launched =
                        match HeadlessCampaign.launch configPath dataRoot "codex" with
                        | Ok status -> status
                        | Error error -> failwith error

                    Assert.True(launched.IsRunning, $"Unexpected launch status: {launched}")
                    Assert.True(launched.ProcessId.IsSome)
                    Assert.Equal(Ok(), HeadlessCampaign.requestStop configPath dataRoot)
                finally
                    Environment.SetEnvironmentVariable("FSHARNESS_CLI_PATH", previous)
                    Directory.Delete(directory, true))

    [<Fact>]
    let ``headless campaign detects an active external durable run and its control file`` () =
        let directory =
            Path.Combine(Path.GetTempPath(), $"fsharness-external-{Guid.NewGuid():N}")

        let configPath = Path.Combine(directory, "campaign.json")
        let dataRoot = Path.Combine(directory, "data")
        let databasePath = Path.Combine(dataRoot, "fsharness.db")
        let activityDirectory = Path.Combine(dataRoot, "launches", "manual")

        Directory.CreateDirectory activityDirectory |> ignore
        File.WriteAllText(configPath, "{}")
        File.WriteAllText(Path.Combine(activityDirectory, "activity.log"), "run started")

        use database = new SqliteConnection($"Data Source={databasePath}")
        database.Open()

        use create = database.CreateCommand()

        create.CommandText <-
            """
            CREATE TABLE runs(
                id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """

        create.ExecuteNonQuery() |> ignore

        let now = DateTimeOffset.UtcNow
        use insert = database.CreateCommand()

        insert.CommandText <-
            "INSERT INTO runs(id, status, created_at, updated_at) VALUES ('run', 'Running', $created, $updated);"

        insert.Parameters.AddWithValue("$created", now.ToString("O")) |> ignore
        insert.Parameters.AddWithValue("$updated", now.ToString("O")) |> ignore
        insert.ExecuteNonQuery() |> ignore
        database.Close()

        try
            let status = HeadlessCampaign.status configPath dataRoot

            Assert.True(status.IsRunning)
            Assert.False(status.IsManagedProcess)
            Assert.Equal(None, status.ProcessId)
            Assert.Equal(Some(Path.Combine(activityDirectory, "stop.request")), status.ControlPath)
            Assert.Equal(Ok(), HeadlessCampaign.requestStop configPath dataRoot)
            Assert.True(File.Exists(Path.Combine(activityDirectory, "stop.request")))
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    let ``evolution monitor polling leaves the manual graph model unchanged`` () =
        use runtime = new HarnessRuntime("codex")
        let model, _ = AppState.init runtime

        let evolutionModel =
            { model with
                Page = Evolution
                EvolutionRequestId = 42
                EvolutionBusy = false }

        let updated, _ = update runtime PollMonitor evolutionModel
        Assert.Equal(evolutionModel, updated)

    [<Fact>]
    let ``evolution page constructs a selectable graph and score view`` () =
        use runtime = new HarnessRuntime("codex")
        let model, _ = AppState.init runtime
        let runId = RunId.create ()
        let experimentId = ExperimentId.create ()
        let baseline = CommitOid.create (String('a', 40))
        let candidate = CommitOid.create (String('b', 40))
        let now = DateTimeOffset.UtcNow

        let run =
            { Id = runId
              SourcePath = "C:\\source"
              Status = "Completed"
              CreatedAt = now
              UpdatedAt = now
              MetricName = "primary"
              Direction = Maximize
              BaselineCommit = Some baseline
              BaselineScore = Some 1M
              FrontierScore = Some 2M
              AttemptCount = 1
              AcceptedCount = 1 }

        let snapshot =
            { Run = run
              Frontier = Some candidate
              Edges = []
              Champion = Some candidate
              ActiveHeads = Set.singleton candidate
              Warnings = []
              Nodes =
                [ { Id = ExperimentNode experimentId
                    Kind = EvolutionNodeKind.Candidate
                    Sequence = 1
                    Parent = Some baseline
                    Commit = Some candidate
                    Outcome = EvolutionOutcome.Accepted
                    Metric = Some 2M
                    RetainedScore = Some 2M
                    Summary = None
                    EvaluationSummary = Some "ok"
                    Usage = None
                    StartedAt = now
                    UpdatedAt = now
                    Label = "Attempt 1" } ] }

        let evolutionModel =
            { model with
                Page = Evolution
                EvolutionRuns = [ run ]
                SelectedEvolutionRun = Some runId
                Evolution = Some snapshot }

        Assert.NotNull(Views.view evolutionModel ignore)

    [<Fact>]
    let ``evolution layout renders siblings and real synthesis as a diamond`` () =
        let now = DateTimeOffset.UtcNow
        let runId = RunId.create ()
        let baseline = CommitOid.create (String('a', 40))
        let first = CommitOid.create (String('b', 40))
        let second = CommitOid.create (String('c', 40))
        let synthesis = CommitOid.create (String('d', 40))

        let node sequence commit parent metric =
            { Id = ExperimentNode(ExperimentId.create ())
              Kind = EvolutionNodeKind.Candidate
              Sequence = sequence
              Parent = Some parent
              Commit = Some commit
              Outcome = EvolutionOutcome.Rejected "valid retained"
              Metric = Some metric
              RetainedScore = Some metric
              Summary = None
              EvaluationSummary = Some "ok"
              Usage = None
              StartedAt = now
              UpdatedAt = now
              Label = $"Attempt {sequence}" }

        let firstNode = node 1 first baseline 2M
        let secondNode = node 2 second baseline 1.5M

        let synthesisNode =
            { node 3 synthesis first 3M with
                Outcome = EvolutionOutcome.Accepted }

        let edge parent child kind role =
            { Id = $"{CommitOid.value parent}:{CommitOid.value child}:{role}"
              Parent = parent
              Child = child
              Kind = kind
              Role = role }

        let snapshot =
            { Run =
                { Id = runId
                  SourcePath = "/source"
                  Status = "Completed"
                  CreatedAt = now
                  UpdatedAt = now
                  MetricName = "primary"
                  Direction = Maximize
                  BaselineCommit = Some baseline
                  BaselineScore = Some 1M
                  FrontierScore = Some 3M
                  AttemptCount = 3
                  AcceptedCount = 1 }
              Frontier = Some synthesis
              Champion = Some synthesis
              ActiveHeads = Set [ first; second; synthesis ]
              Edges =
                [ edge baseline first ExperimentKind.Expansion ExperimentParentRole.Primary
                  edge baseline second ExperimentKind.Expansion ExperimentParentRole.Primary
                  edge first synthesis ExperimentKind.Synthesis ExperimentParentRole.Primary
                  edge second synthesis ExperimentKind.Synthesis ExperimentParentRole.Contributor ]
              Warnings = []
              Nodes = [ firstNode; secondNode; synthesisNode ] }

        let layout = EvolutionLayout.build snapshot

        let firstPosition =
            layout.Nodes |> List.find (fun item -> item.Node.Commit = Some first)

        let secondPosition =
            layout.Nodes |> List.find (fun item -> item.Node.Commit = Some second)

        let synthesisPosition =
            layout.Nodes |> List.find (fun item -> item.Node.Commit = Some synthesis)

        Assert.Equal(4, layout.Edges.Length)
        Assert.Equal(firstPosition.X, secondPosition.X)
        Assert.NotEqual(firstPosition.Y, secondPosition.Y)
        Assert.True(synthesisPosition.X > firstPosition.X)
        Assert.True synthesisPosition.IsSynthesis
        Assert.True synthesisPosition.IsChampion
        Assert.Equal(3, layout.Scores.Length)

    [<Fact>]
    let ``shell renders at both supported viewport sizes and accepts keyboard focus`` () =
        use session = HeadlessUnitTestSession.StartNew(typeof<HeadlessAppBuilder>)

        session
            .Dispatch(
                Action(fun () ->
                    for width, height in [ 1024.0, 680.0; 1440.0, 900.0 ] do
                        let window = new MainWindow()
                        window.Width <- width
                        window.Height <- height
                        window.Show()

                        use frame = window.CaptureRenderedFrame()
                        Assert.NotNull frame
                        Assert.True(frame.PixelSize.Width >= int width)
                        Assert.True(frame.PixelSize.Height >= int height)

                        let browse =
                            window.GetVisualDescendants()
                            |> Seq.choose (function
                                | :? Button as button when string button.Content = "Browse…" -> Some button
                                | _ -> None)
                            |> Seq.tryExactlyOne

                        Assert.True(browse.IsSome, "The repository field should expose a folder picker.")

                        let dataRootBrowse =
                            window.GetVisualDescendants()
                            |> Seq.choose (function
                                | :? Button as button when string button.Content = "Browse data root" -> Some button
                                | _ -> None)
                            |> Seq.tryExactlyOne

                        Assert.True(dataRootBrowse.IsSome, "The data-root field should expose a folder picker.")

                        let loadExperiment =
                            window.GetVisualDescendants()
                            |> Seq.choose (function
                                | :? Button as button when string button.Content = "Load experiment…" -> Some button
                                | _ -> None)
                            |> Seq.tryExactlyOne

                        Assert.True(loadExperiment.IsSome, "Setup should expose experiment loading.")

                        let evolution =
                            window.GetVisualDescendants()
                            |> Seq.choose (function
                                | :? Button as button when string button.Content = "Evolution" -> Some button
                                | _ -> None)
                            |> Seq.tryExactlyOne

                        Assert.True(evolution.IsSome, "The shell should expose the Evolution page.")

                        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None)
                        Assert.NotNull(window.FocusManager.GetFocusedElement())
                        window.Close()),
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult()

    [<Fact>]
    let ``folder selection invalidates repository inspection and setup remains renderable`` () =
        use runtime = new HarnessRuntime("codex")
        let model, _ = AppState.init runtime

        let inspected =
            { TopLevel = "C:\\old"
              CommonDirectory = "C:\\old\\.git"
              Head = CommitOid.create (String('a', 40))
              Branch = Some "main"
              IsDirty = false
              DirtySummary = ""
              HasSubmodules = false
              IsSparseCheckout = false }

        let selectedPath = "C:\\selected"

        let selected, _ =
            AppState.update
                runtime
                (fun () -> async { return Ok None })
                (fun () -> async { return Ok None })
                (fun () -> async { return Ok None })
                (fun _ _ -> async { return Ok None })
                "codex"
                (RepositoryFolderSelected(Ok(Some selectedPath)))
                { model with
                    Repository = Some inspected }

        Assert.Equal(selectedPath, selected.Draft.SourcePath)
        Assert.True(selected.Repository.IsNone)
        Assert.NotNull(Views.view selected ignore)

        let loadedConfig =
            { SchemaVersion = HarnessConfig.currentSchemaVersion
              SourcePath = selectedPath
              BaseCommit = inspected.Head
              Objective = "Loaded objective"
              EditablePaths = [ "src/**" ]
              SeedPatches = [ ".fsharness/seeds/zero.patch" ]
              Evaluator =
                { Executable = "python"
                  Arguments = [ ".fsharness/evaluate.py" ]
                  WorkingDirectory = "."
                  Timeout = TimeSpan.FromHours 3.0
                  RequiredConstraints = [ "tests" ]
                  MaxInconclusiveRetries = 2
                  MaxInfrastructureRetries = 120
                  InfrastructureRetryDelay = TimeSpan.FromSeconds 15.0 }
              Metric =
                { Name = "paired_speed_index_lcb"
                  Direction = Maximize
                  MinDelta = 2M
                  Target = Some 110M
                  Comparison = EvaluationMetric "frontier_speed_index" }
              Model =
                { Id = "gpt-5.6-sol"
                  Effort = ReasoningEffort.Medium }
              PromptProfile =
                { MaxMemoryCount = 2
                  MaxMemoryCharacters = 2_000
                  MaxEvaluationFindings = 3
                  MaxEvaluationCharacters = 1_000 }
              GraphSearch = Defaults.graphSearch
              Budgets =
                { Defaults.budgets with
                    MaxExperiments = 3
                    MaxRawTokens = 3_000_000L }
              PromotionMode = AutoWhenStrictlyBetter }

        let loaded, _ =
            AppState.update
                runtime
                (fun () -> async { return Ok None })
                (fun () -> async { return Ok None })
                (fun () -> async { return Ok None })
                (fun _ _ -> async { return Ok None })
                "codex"
                (ExperimentLoaded(Ok(Some("C:\\experiment.json", loadedConfig, None))))
                selected

        Assert.Equal("Loaded objective", loaded.Draft.Objective)
        Assert.Equal(EvaluationMetric "frontier_speed_index", loaded.Draft.MetricComparison)
        Assert.Equal(TimeSpan.FromHours 3.0, loaded.Draft.EvaluatorTimeout)
        Assert.Equal<string list>([ ".fsharness/seeds/zero.patch" ], loaded.Draft.SeedPatches)
        Assert.True(loaded.Repository.IsNone)

    [<Fact>]
    let ``async command results render on the Avalonia dispatcher`` () =
        use session = HeadlessUnitTestSession.StartNew(typeof<HeadlessAppBuilder>)
        use rendered = new ManualResetEventSlim(false)
        let mutable renderedOnUiThread = false
        let mutable window: HostWindow option = None

        session
            .Dispatch(
                Action(fun () ->
                    let current = new HostWindow()
                    window <- Some current

                    let init () =
                        false,
                        Cmd.OfAsync.perform
                            (fun () ->
                                async {
                                    do! Async.Sleep 25
                                    return ()
                                })
                            ()
                            id

                    let update () _ = true, Cmd.none

                    let view completed _ =
                        if completed then
                            renderedOnUiThread <- Dispatcher.UIThread.CheckAccess()
                            rendered.Set()

                        Border.create []

                    Elmish.Program.mkProgram init update view
                    |> Avalonia.FuncUI.Elmish.Program.withHost current
                    |> Avalonia.FuncUI.Elmish.Program.runWithAvaloniaSyncDispatch ()

                    current.Show()),
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult()

        Assert.True(rendered.Wait(TimeSpan.FromSeconds 5.0), "The asynchronous Elmish command did not complete.")
        Assert.True(renderedOnUiThread, "The resulting view was constructed off the Avalonia dispatcher.")

        session
            .Dispatch(Action(fun () -> window |> Option.iter (fun current -> current.Close())), CancellationToken.None)
            .GetAwaiter()
            .GetResult()
