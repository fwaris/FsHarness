namespace FsHarness.App

open System
open System.IO
open System.Threading
open Elmish
open FsHarness.Core
open FsHarness.Infrastructure

type Page =
    | Setup
    | CurrentRun
    | Evolution
    | History
    | Settings

type DraftConfig =
    { SourcePath: string
      Objective: string
      EditablePaths: string
      SeedPatches: string list
      EvaluatorExecutable: string
      EvaluatorArguments: string
      EvaluatorWorkingDirectory: string
      EvaluatorTimeout: TimeSpan
      MaxInconclusiveRetries: int
      RequiredConstraints: string
      MetricName: string
      MetricDirection: MetricDirection
      MinDelta: string
      MetricTarget: decimal option
      MetricComparison: MetricComparison
      ModelId: string
      ReasoningEffort: ReasoningEffort
      PromptProfile: PromptProfile
      MaxExperiments: string
      MaxRawTokens: string
      MaxDuration: TimeSpan
      CodexTimeout: TimeSpan
      MaxConsecutiveNonImprovements: int
      MaxConsecutiveFailures: int
      PromotionMode: PromotionMode }

type Model =
    { Page: Page
      DataRoot: string
      Draft: DraftConfig
      Repository: RepositoryInspection option
      CodexHealth: CodexPreflight option
      Prepared: PreparedRunReport option
      Run: RunState option
      Activities: RuntimeActivity list
      History: HistoryEvent list
      EvolutionRuns: EvolutionRunSummary list
      SelectedEvolutionRun: RunId option
      Evolution: EvolutionSnapshot option
      SelectedEvolutionNode: EvolutionNodeId option
      EvolutionBusy: bool
      EvolutionRequestId: int
      Busy: bool
      ExperimentFile: string option
      Error: string option }

type DraftField =
    | SourcePath
    | Objective
    | EditablePaths
    | EvaluatorExecutable
    | EvaluatorArguments
    | EvaluatorWorkingDirectory
    | RequiredConstraints
    | MetricName
    | MinDelta
    | ModelId
    | MaxExperiments
    | MaxRawTokens

type Msg =
    | Navigate of Page
    | DraftChanged of DraftField * string
    | DataRootChanged of string
    | BrowseRepository
    | RepositoryFolderSelected of Result<string option, string>
    | BrowseDataRoot
    | DataRootSelected of Result<string option, string>
    | LoadExperiment
    | ExperimentLoaded of Result<(string * HarnessConfig) option, string>
    | SaveExperiment
    | ExperimentSaved of Result<string option, string>
    | ToggleMetricDirection
    | TogglePromotionMode
    | InspectRepository
    | RepositoryInspected of Result<RepositoryInspection, HarnessError>
    | CheckCodex
    | CodexChecked of Result<CodexPreflight, HarnessError>
    | PrepareRun
    | RunPrepared of Result<PreparedRunReport, HarnessError>
    | StartRun
    | PauseAfterCurrent
    | ResumeRun
    | StopNow
    | AcceptWinner
    | RejectWinner
    | ReviewCompleted of Result<unit, HarnessError>
    | RuntimeStateChanged of RunState
    | RuntimeActivityReceived of RuntimeActivity
    | RuntimeEvolutionChanged of RunId
    | LoadEvolutionRuns
    | EvolutionRunsLoaded of Result<EvolutionRunSummary list, HarnessError>
    | SelectEvolutionRun of RunId
    | EvolutionLoaded of int * RunId * Result<EvolutionSnapshot, HarnessError>
    | RefreshEvolution
    | SelectEvolutionNode of EvolutionNodeId
    | RefreshHistory
    | HistoryLoaded of Result<HistoryEvent list, HarnessError>
    | ClearError

[<RequireQualifiedAccess>]
module AppState =
    let private errorText error =
        match error.Detail with
        | Some detail -> $"{error.Summary} {detail}"
        | None -> error.Summary

    let private defaultDraft =
        { SourcePath = Directory.GetCurrentDirectory()
          Objective = "Improve the primary metric with one small, maintainable change while preserving all constraints."
          EditablePaths = "src/**;tests/**"
          SeedPatches = []
          EvaluatorExecutable = "dotnet"
          EvaluatorArguments = "run\n--project\n.fsharness/Evaluator.fsproj\n--configuration\nRelease\n--no-restore"
          EvaluatorWorkingDirectory = "."
          EvaluatorTimeout = TimeSpan.FromMinutes 15.0
          MaxInconclusiveRetries = 2
          RequiredConstraints = "build;tests"
          MetricName = "primary"
          MetricDirection = Maximize
          MinDelta = "0"
          MetricTarget = None
          MetricComparison = RetainedScore
          ModelId = Defaults.model.Id
          ReasoningEffort = Defaults.model.Effort
          PromptProfile = Defaults.promptProfile
          MaxExperiments = string Defaults.budgets.MaxExperiments
          MaxRawTokens = string Defaults.budgets.MaxRawTokens
          MaxDuration = Defaults.budgets.MaxDuration
          CodexTimeout = Defaults.budgets.CodexTimeout
          MaxConsecutiveNonImprovements = Defaults.budgets.MaxConsecutiveNonImprovements
          MaxConsecutiveFailures = Defaults.budgets.MaxConsecutiveFailures
          PromotionMode = AutoWhenStrictlyBetter }

    let private draftFromConfig (config: HarnessConfig) =
        { SourcePath = config.SourcePath
          Objective = config.Objective
          EditablePaths = String.concat ";" config.EditablePaths
          SeedPatches = config.SeedPatches
          EvaluatorExecutable = config.Evaluator.Executable
          EvaluatorArguments = String.concat Environment.NewLine config.Evaluator.Arguments
          EvaluatorWorkingDirectory = config.Evaluator.WorkingDirectory
          EvaluatorTimeout = config.Evaluator.Timeout
          MaxInconclusiveRetries = config.Evaluator.MaxInconclusiveRetries
          RequiredConstraints = String.concat ";" config.Evaluator.RequiredConstraints
          MetricName = config.Metric.Name
          MetricDirection = config.Metric.Direction
          MinDelta = string config.Metric.MinDelta
          MetricTarget = config.Metric.Target
          MetricComparison = config.Metric.Comparison
          ModelId = config.Model.Id
          ReasoningEffort = config.Model.Effort
          PromptProfile = config.PromptProfile
          MaxExperiments = string config.Budgets.MaxExperiments
          MaxRawTokens = string config.Budgets.MaxRawTokens
          MaxDuration = config.Budgets.MaxDuration
          CodexTimeout = config.Budgets.CodexTimeout
          MaxConsecutiveNonImprovements = config.Budgets.MaxConsecutiveNonImprovements
          MaxConsecutiveFailures = config.Budgets.MaxConsecutiveFailures
          PromotionMode = config.PromotionMode }

    let private subscribe (runtime: HarnessRuntime) =
        [ fun dispatch ->
              runtime.StateChanged.Add(RuntimeStateChanged >> dispatch)
              runtime.Activity.Add(RuntimeActivityReceived >> dispatch)
              runtime.EvolutionChanged.Add(RuntimeEvolutionChanged >> dispatch) ]

    let init (runtime: HarnessRuntime) =
        { Page = Setup
          DataRoot = runtime.DataRoot
          Draft = defaultDraft
          Repository = None
          CodexHealth = None
          Prepared = None
          Run = runtime.State
          Activities = []
          History = []
          EvolutionRuns = []
          SelectedEvolutionRun = None
          Evolution = None
          SelectedEvolutionNode = None
          EvolutionBusy = false
          EvolutionRequestId = 0
          Busy = false
          ExperimentFile = None
          Error = None },
        subscribe runtime

    let private updateDraft (field: DraftField) (value: string) (draft: DraftConfig) =
        match field with
        | SourcePath -> { draft with SourcePath = value }
        | Objective -> { draft with Objective = value }
        | EditablePaths -> { draft with EditablePaths = value }
        | EvaluatorExecutable ->
            { draft with
                EvaluatorExecutable = value }
        | EvaluatorArguments ->
            { draft with
                EvaluatorArguments = value }
        | EvaluatorWorkingDirectory ->
            { draft with
                EvaluatorWorkingDirectory = value }
        | RequiredConstraints ->
            { draft with
                RequiredConstraints = value }
        | MetricName -> { draft with MetricName = value }
        | MinDelta -> { draft with MinDelta = value }
        | ModelId -> { draft with ModelId = value }
        | MaxExperiments -> { draft with MaxExperiments = value }
        | MaxRawTokens -> { draft with MaxRawTokens = value }

    let private splitSemicolon (value: string) =
        value.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> List.ofArray

    let private splitLines (value: string) =
        value.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> List.ofArray

    let private createConfig (model: Model) =
        match model.Repository with
        | None -> Error "Inspect the repository before preparing a run."
        | Some repository ->
            match
                Decimal.TryParse model.Draft.MinDelta,
                Int32.TryParse model.Draft.MaxExperiments,
                Int64.TryParse model.Draft.MaxRawTokens
            with
            | (true, minDelta), (true, maxExperiments), (true, maxRawTokens) ->
                let config =
                    { SchemaVersion = HarnessConfig.currentSchemaVersion
                      SourcePath = model.Draft.SourcePath
                      BaseCommit = repository.Head
                      Objective = model.Draft.Objective
                      EditablePaths = splitSemicolon model.Draft.EditablePaths
                      SeedPatches = model.Draft.SeedPatches
                      Evaluator =
                        { Executable = model.Draft.EvaluatorExecutable
                          Arguments = splitLines model.Draft.EvaluatorArguments
                          WorkingDirectory = model.Draft.EvaluatorWorkingDirectory
                          Timeout = model.Draft.EvaluatorTimeout
                          RequiredConstraints = splitSemicolon model.Draft.RequiredConstraints
                          MaxInconclusiveRetries = model.Draft.MaxInconclusiveRetries }
                      Metric =
                        { Name = model.Draft.MetricName
                          Direction = model.Draft.MetricDirection
                          MinDelta = minDelta
                          Target = model.Draft.MetricTarget
                          Comparison = model.Draft.MetricComparison }
                      Model =
                        { Id = model.Draft.ModelId
                          Effort = model.Draft.ReasoningEffort }
                      PromptProfile = model.Draft.PromptProfile
                      Budgets =
                        { Defaults.budgets with
                            MaxExperiments = maxExperiments
                            MaxRawTokens = maxRawTokens
                            MaxDuration = model.Draft.MaxDuration
                            CodexTimeout = model.Draft.CodexTimeout
                            MaxConsecutiveNonImprovements = model.Draft.MaxConsecutiveNonImprovements
                            MaxConsecutiveFailures = model.Draft.MaxConsecutiveFailures }
                      PromotionMode = model.Draft.PromotionMode }

                match HarnessConfig.validate config with
                | Ok value -> Ok value
                | Error errors -> Error(String.concat " " errors)
            | _ -> Error "Min delta, maximum experiments, and raw-token budget must be valid numbers."

    let update
        (runtime: HarnessRuntime)
        (pickRepositoryFolder: unit -> Async<Result<string option, string>>)
        (pickDataRootFolder: unit -> Async<Result<string option, string>>)
        (loadExperiment: unit -> Async<Result<(string * HarnessConfig) option, string>>)
        (saveExperiment: HarnessConfig -> Async<Result<string option, string>>)
        (message: Msg)
        (model: Model)
        =
        let loadEvolution runId requestId =
            Cmd.OfAsync.perform (fun () -> runtime.LoadEvolution(runId, CancellationToken.None)) () (fun result ->
                EvolutionLoaded(requestId, runId, result))

        match message with
        | Navigate page ->
            let command =
                if page = History then Cmd.ofMsg RefreshHistory
                elif page = Evolution then Cmd.ofMsg LoadEvolutionRuns
                else Cmd.none

            { model with Page = page }, command
        | DraftChanged(field, value) ->
            let updated =
                { model with
                    Draft = updateDraft field value model.Draft
                    Prepared = None
                    ExperimentFile = None
                    Error = None }

            match field with
            | DraftField.SourcePath -> { updated with Repository = None }, Cmd.none
            | _ -> updated, Cmd.none
        | DataRootChanged value ->
            { model with
                DataRoot = value
                Error = None },
            Cmd.none
        | BrowseRepository ->
            { model with Error = None },
            Cmd.OfAsync.perform (fun () -> pickRepositoryFolder ()) () RepositoryFolderSelected
        | RepositoryFolderSelected result ->
            match result with
            | Ok(Some path) ->
                { model with
                    Draft = { model.Draft with SourcePath = path }
                    Repository = None
                    Prepared = None
                    ExperimentFile = None
                    Error = None },
                Cmd.none
            | Ok None -> model, Cmd.none
            | Error error -> { model with Error = Some error }, Cmd.none
        | BrowseDataRoot ->
            { model with Error = None }, Cmd.OfAsync.perform (fun () -> pickDataRootFolder ()) () DataRootSelected
        | DataRootSelected result ->
            match result with
            | Ok(Some path) ->
                { model with
                    DataRoot = path
                    Error = None },
                Cmd.none
            | Ok None -> model, Cmd.none
            | Error error -> { model with Error = Some error }, Cmd.none
        | LoadExperiment ->
            { model with Busy = true; Error = None },
            Cmd.OfAsync.perform (fun () -> loadExperiment ()) () ExperimentLoaded
        | ExperimentLoaded result ->
            match result with
            | Ok(Some(path, config)) ->
                { model with
                    Draft = draftFromConfig config
                    Repository = None
                    CodexHealth = None
                    Prepared = None
                    Busy = false
                    ExperimentFile = Some path
                    Error = None },
                Cmd.none
            | Ok None -> { model with Busy = false }, Cmd.none
            | Error error ->
                { model with
                    Busy = false
                    Error = Some error },
                Cmd.none
        | SaveExperiment ->
            match createConfig model with
            | Error error -> { model with Error = Some error }, Cmd.none
            | Ok config ->
                { model with Busy = true; Error = None },
                Cmd.OfAsync.perform (fun () -> saveExperiment config) () ExperimentSaved
        | ExperimentSaved result ->
            match result with
            | Ok(Some path) ->
                { model with
                    Busy = false
                    ExperimentFile = Some path
                    Error = None },
                Cmd.none
            | Ok None -> { model with Busy = false }, Cmd.none
            | Error error ->
                { model with
                    Busy = false
                    Error = Some error },
                Cmd.none
        | ToggleMetricDirection ->
            let direction =
                match model.Draft.MetricDirection with
                | Maximize -> Minimize
                | Minimize -> Maximize

            { model with
                Draft =
                    { model.Draft with
                        MetricDirection = direction } },
            Cmd.none
        | TogglePromotionMode ->
            let promotion =
                match model.Draft.PromotionMode with
                | AutoWhenStrictlyBetter -> ReviewStrictWinners
                | ReviewStrictWinners -> AutoWhenStrictlyBetter

            { model with
                Draft =
                    { model.Draft with
                        PromotionMode = promotion } },
            Cmd.none
        | InspectRepository ->
            { model with Busy = true; Error = None },
            Cmd.OfAsync.perform
                (fun () -> runtime.InspectSource(model.Draft.SourcePath, CancellationToken.None))
                ()
                RepositoryInspected
        | RepositoryInspected result ->
            match result with
            | Ok repository ->
                { model with
                    Repository = Some repository
                    Prepared = None
                    Busy = false
                    Error = None },
                Cmd.none
            | Error error ->
                { model with
                    Busy = false
                    Error = Some(errorText error) },
                Cmd.none
        | CheckCodex ->
            { model with Busy = true; Error = None },
            Cmd.OfAsync.perform (fun () -> runtime.CheckCodex CancellationToken.None) () CodexChecked
        | CodexChecked result ->
            match result with
            | Ok report ->
                { model with
                    CodexHealth = Some report
                    Busy = false
                    Error = None },
                Cmd.none
            | Error error ->
                { model with
                    Busy = false
                    Error = Some(errorText error) },
                Cmd.none
        | PrepareRun ->
            match runtime.TrySetDataRoot model.DataRoot with
            | Error error -> { model with Error = Some error }, Cmd.none
            | Ok appliedRoot ->
                match createConfig model with
                | Error error -> { model with Error = Some error }, Cmd.none
                | Ok config ->
                    { model with
                        DataRoot = appliedRoot
                        Busy = true
                        Error = None },
                    Cmd.OfAsync.perform (fun () -> runtime.Prepare(config, CancellationToken.None)) () RunPrepared
        | RunPrepared result ->
            match result with
            | Ok report ->
                { model with
                    Prepared = Some report
                    CodexHealth = Some report.Codex
                    Run = runtime.State
                    Busy = false
                    Error = None },
                Cmd.none
            | Error error ->
                { model with
                    Busy = false
                    Error = Some(errorText error) },
                Cmd.none
        | StartRun ->
            match runtime.Start() with
            | Ok _ ->
                { model with
                    Page = CurrentRun
                    Error = None },
                Cmd.none
            | Error error ->
                { model with
                    Error = Some(errorText error) },
                Cmd.none
        | PauseAfterCurrent ->
            runtime.PauseAfterCurrent()
            model, Cmd.none
        | ResumeRun ->
            runtime.Resume()
            model, Cmd.none
        | StopNow ->
            runtime.StopNow()
            model, Cmd.none
        | AcceptWinner ->
            { model with Busy = true; Error = None },
            Cmd.OfAsync.perform (fun () -> runtime.ReviewWinner true) () ReviewCompleted
        | RejectWinner ->
            { model with Busy = true; Error = None },
            Cmd.OfAsync.perform (fun () -> runtime.ReviewWinner false) () ReviewCompleted
        | ReviewCompleted result ->
            match result with
            | Ok() ->
                { model with
                    Busy = false
                    Error = None },
                Cmd.none
            | Error error ->
                { model with
                    Busy = false
                    Error = Some(errorText error) },
                Cmd.none
        | RuntimeStateChanged run ->
            let shouldRefresh =
                model.Page = Evolution && model.SelectedEvolutionRun = Some run.Id

            let nextRequest =
                if shouldRefresh then
                    model.EvolutionRequestId + 1
                else
                    model.EvolutionRequestId

            let command =
                if shouldRefresh then
                    loadEvolution run.Id nextRequest
                else
                    Cmd.none

            { model with
                Run = Some run
                EvolutionRequestId = nextRequest
                EvolutionBusy = shouldRefresh || model.EvolutionBusy },
            command
        | RuntimeActivityReceived event ->
            { model with
                Activities = event :: model.Activities |> List.truncate 2_000 },
            Cmd.none
        | RuntimeEvolutionChanged runId ->
            if model.Page = Evolution && model.SelectedEvolutionRun = Some runId then
                let requestId = model.EvolutionRequestId + 1

                { model with
                    EvolutionRequestId = requestId
                    EvolutionBusy = true },
                loadEvolution runId requestId
            else
                model, Cmd.none
        | LoadEvolutionRuns ->
            { model with
                EvolutionBusy = true
                Error = None },
            Cmd.OfAsync.perform (fun () -> runtime.ListEvolutionRuns CancellationToken.None) () EvolutionRunsLoaded
        | EvolutionRunsLoaded result ->
            match result with
            | Error error ->
                { model with
                    EvolutionBusy = false
                    Error = Some(errorText error) },
                Cmd.none
            | Ok runs ->
                let selected =
                    model.SelectedEvolutionRun
                    |> Option.filter (fun id -> runs |> List.exists (fun item -> item.Id = id))
                    |> Option.orElseWith (fun () ->
                        model.Run
                        |> Option.map _.Id
                        |> Option.filter (fun id -> runs |> List.exists (fun item -> item.Id = id)))
                    |> Option.orElseWith (fun () -> runs |> List.tryHead |> Option.map _.Id)

                let next =
                    { model with
                        EvolutionRuns = runs
                        SelectedEvolutionRun = selected
                        Evolution = None
                        SelectedEvolutionNode = None
                        EvolutionBusy = selected.IsSome
                        Error = None }

                match selected with
                | Some runId ->
                    let requestId = model.EvolutionRequestId + 1

                    { next with
                        EvolutionRequestId = requestId },
                    loadEvolution runId requestId
                | None -> { next with EvolutionBusy = false }, Cmd.none
        | SelectEvolutionRun runId ->
            let requestId = model.EvolutionRequestId + 1

            { model with
                SelectedEvolutionRun = Some runId
                Evolution = None
                SelectedEvolutionNode = None
                EvolutionBusy = true
                EvolutionRequestId = requestId
                Error = None },
            loadEvolution runId requestId
        | EvolutionLoaded(requestId, runId, result) when requestId = model.EvolutionRequestId ->
            match result with
            | Ok snapshot when model.SelectedEvolutionRun = Some runId ->
                { model with
                    EvolutionRuns =
                        model.EvolutionRuns
                        |> List.map (fun run -> if run.Id = runId then snapshot.Run else run)
                    Evolution = Some snapshot
                    EvolutionBusy = false
                    Error = None },
                Cmd.none
            | Error error when model.SelectedEvolutionRun = Some runId ->
                { model with
                    Evolution = None
                    EvolutionBusy = false
                    Error = Some(errorText error) },
                Cmd.none
            | _ -> model, Cmd.none
        | EvolutionLoaded _ -> model, Cmd.none
        | RefreshEvolution ->
            match model.SelectedEvolutionRun with
            | None -> model, Cmd.ofMsg LoadEvolutionRuns
            | Some runId ->
                let requestId = model.EvolutionRequestId + 1

                { model with
                    EvolutionRequestId = requestId
                    EvolutionBusy = true },
                loadEvolution runId requestId
        | SelectEvolutionNode nodeId ->
            { model with
                SelectedEvolutionNode = Some nodeId },
            Cmd.none
        | RefreshHistory -> { model with Busy = true }, Cmd.ofMsg (HistoryLoaded(runtime.History 500))
        | HistoryLoaded result ->
            match result with
            | Ok events ->
                { model with
                    History = events
                    Busy = false },
                Cmd.none
            | Error error ->
                { model with
                    Busy = false
                    Error = Some(errorText error) },
                Cmd.none
        | ClearError -> { model with Error = None }, Cmd.none
