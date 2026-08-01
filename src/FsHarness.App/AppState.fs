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
    | History
    | Settings

type DraftConfig =
    { SourcePath: string
      Objective: string
      EditablePaths: string
      EvaluatorExecutable: string
      EvaluatorArguments: string
      EvaluatorWorkingDirectory: string
      RequiredConstraints: string
      MetricName: string
      MetricDirection: MetricDirection
      MinDelta: string
      ModelId: string
      ReasoningEffort: ReasoningEffort
      MaxExperiments: string
      MaxRawTokens: string
      PromotionMode: PromotionMode }

type Model =
    { Page: Page
      Draft: DraftConfig
      Repository: RepositoryInspection option
      CodexHealth: CodexPreflight option
      Prepared: PreparedRunReport option
      Run: RunState option
      Activities: RuntimeActivity list
      History: HistoryEvent list
      Busy: bool
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
          EvaluatorExecutable = "dotnet"
          EvaluatorArguments = "run\n--project\n.fsharness/Evaluator.fsproj\n--configuration\nRelease\n--no-restore"
          EvaluatorWorkingDirectory = "."
          RequiredConstraints = "build;tests"
          MetricName = "primary"
          MetricDirection = Maximize
          MinDelta = "0"
          ModelId = Defaults.model.Id
          ReasoningEffort = Defaults.model.Effort
          MaxExperiments = string Defaults.budgets.MaxExperiments
          MaxRawTokens = string Defaults.budgets.MaxRawTokens
          PromotionMode = AutoWhenStrictlyBetter }

    let private subscribe (runtime: HarnessRuntime) =
        [ fun dispatch ->
              runtime.StateChanged.Add(RuntimeStateChanged >> dispatch)
              runtime.Activity.Add(RuntimeActivityReceived >> dispatch) ]

    let init (runtime: HarnessRuntime) =
        { Page = Setup
          Draft = defaultDraft
          Repository = None
          CodexHealth = None
          Prepared = None
          Run = runtime.State
          Activities = []
          History = []
          Busy = false
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
                      SeedPatches = []
                      Evaluator =
                        { Executable = model.Draft.EvaluatorExecutable
                          Arguments = splitLines model.Draft.EvaluatorArguments
                          WorkingDirectory = model.Draft.EvaluatorWorkingDirectory
                          Timeout = TimeSpan.FromMinutes 15.0
                          RequiredConstraints = splitSemicolon model.Draft.RequiredConstraints
                          MaxInconclusiveRetries = 2 }
                      Metric =
                        { Name = model.Draft.MetricName
                          Direction = model.Draft.MetricDirection
                          MinDelta = minDelta
                          Target = None
                          Comparison = RetainedScore }
                      Model =
                        { Id = model.Draft.ModelId
                          Effort = model.Draft.ReasoningEffort }
                      Budgets =
                        { Defaults.budgets with
                            MaxExperiments = maxExperiments
                            MaxRawTokens = maxRawTokens }
                      PromotionMode = model.Draft.PromotionMode }

                match HarnessConfig.validate config with
                | Ok value -> Ok value
                | Error errors -> Error(String.concat " " errors)
            | _ -> Error "Min delta, maximum experiments, and raw-token budget must be valid numbers."

    let update (runtime: HarnessRuntime) (message: Msg) (model: Model) =
        match message with
        | Navigate page ->
            let command =
                if page = History then
                    Cmd.ofMsg RefreshHistory
                else
                    Cmd.none

            { model with Page = page }, command
        | DraftChanged(field, value) ->
            { model with
                Draft = updateDraft field value model.Draft
                Prepared = None
                Error = None },
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
            match createConfig model with
            | Error error -> { model with Error = Some error }, Cmd.none
            | Ok config ->
                { model with Busy = true; Error = None },
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
        | RuntimeStateChanged run -> { model with Run = Some run }, Cmd.none
        | RuntimeActivityReceived event ->
            { model with
                Activities = event :: model.Activities |> List.truncate 2_000 },
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
