namespace FsHarness.Tests

open System
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
open Xunit

type HeadlessAppBuilder =
    static member BuildAvaloniaApp() =
        let options = AvaloniaHeadlessPlatformOptions()
        options.UseHeadlessDrawing <- false
        AppBuilder.Configure<App>().UseSkia().UseHeadless(options)

module UiTests =
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
                (fun _ -> async { return Ok None })
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
                  MaxInconclusiveRetries = 2 }
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
                (fun _ -> async { return Ok None })
                (ExperimentLoaded(Ok(Some("C:\\experiment.json", loadedConfig))))
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
