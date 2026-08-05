namespace FsHarness.App

open System
open System.IO
open System.Threading
open Avalonia.Threading
open Elmish
open FsHarness.Core
open FsHarness.Infrastructure

type Page =
    | Setup
    | CurrentRun
    | Evolution
    | Knowledge
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
      MaxInfrastructureRetries: string
      InfrastructureRetryDelaySeconds: string
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
      DataRootOverridden: bool
      Draft: DraftConfig
      Repository: RepositoryInspection option
      CodexHealth: CodexPreflight option
      Campaign: HeadlessCampaignStatus option
      AssociatedRun: EvolutionRunSummary option
      History: HistoryEvent list
      EvolutionRuns: EvolutionRunSummary list
      SelectedEvolutionRun: RunId option
      Evolution: EvolutionSnapshot option
      Knowledge: RepositoryKnowledgeGraph option
      SelectedEvolutionNode: EvolutionNodeId option
      EvolutionBusy: bool
      KnowledgeBusy: bool
      EvolutionRequestId: int
      FontScale: float
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
    | MaxInfrastructureRetries
    | InfrastructureRetryDelaySeconds
    | RequiredConstraints
    | MetricName
    | MinDelta
    | ModelId
    | MaxExperiments
    | MaxRawTokens

type Msg =
    | Navigate of Page
    | DecreaseFont
    | IncreaseFont
    | DraftChanged of DraftField * string
    | DataRootChanged of string
    | BrowseRepository
    | RepositoryFolderSelected of Result<string option, string>
    | BrowseDataRoot
    | DataRootSelected of Result<string option, string>
    | LoadExperiment
    | ExperimentLoaded of Result<(string * HarnessConfig * string option) option, string>
    | SaveExperiment
    | ExperimentSaved of Result<string option, string>
    | ToggleMetricDirection
    | TogglePromotionMode
    | InspectRepository
    | RepositoryInspected of Result<RepositoryInspection, HarnessError>
    | CheckCodex
    | CodexChecked of Result<CodexPreflight, HarnessError>
    | LaunchCampaign
    | CampaignLaunched of Result<HeadlessCampaignStatus, string>
    | StopCampaign
    | CampaignStopRequested of Result<unit, string>
    | PollMonitor
    | CampaignStatusPolled of HeadlessCampaignStatus option
    | MonitorRunsLoaded of Result<EvolutionRunSummary list, HarnessError>
    | LoadEvolutionRuns
    | EvolutionRunsLoaded of Result<EvolutionRunSummary list, HarnessError>
    | SelectEvolutionRun of RunId
    | EvolutionLoaded of int * RunId * Result<EvolutionSnapshot, HarnessError>
    | RefreshEvolution
    | SelectEvolutionNode of EvolutionNodeId
    | RefreshKnowledge
    | KnowledgeLoaded of RunId * Result<RepositoryKnowledgeGraph, HarnessError>
    | RefreshHistory
    | HistoryLoaded of Result<HistoryEvent list, HarnessError>
    | ClearError

[<RequireQualifiedAccess>]
module AppState =
    let private errorText (error: HarnessError) =
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
          MaxInfrastructureRetries = string Defaults.evaluatorMaxInfrastructureRetries
          InfrastructureRetryDelaySeconds = string Defaults.evaluatorInfrastructureRetryDelay.TotalSeconds
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
          MaxInfrastructureRetries = string config.Evaluator.MaxInfrastructureRetries
          InfrastructureRetryDelaySeconds = string config.Evaluator.InfrastructureRetryDelay.TotalSeconds
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

    let private subscribe () =
        [ fun dispatch ->
              let timer = DispatcherTimer(Interval = TimeSpan.FromSeconds 5.0)
              timer.Tick.Add(fun _ -> dispatch PollMonitor)
              timer.Start() ]

    let init (runtime: HarnessRuntime) =
        { Page = Setup
          DataRoot = runtime.DataRoot
          DataRootOverridden = DataPaths.environmentRoot().IsSome
          Draft = defaultDraft
          Repository = None
          CodexHealth = None
          Campaign = None
          AssociatedRun = None
          History = []
          EvolutionRuns = []
          SelectedEvolutionRun = None
          Evolution = None
          Knowledge = None
          SelectedEvolutionNode = None
          EvolutionBusy = false
          KnowledgeBusy = false
          EvolutionRequestId = 0
          FontScale = 1.0
          Busy = false
          ExperimentFile = None
          Error = None },
        subscribe ()

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
        | MaxInfrastructureRetries ->
            { draft with
                MaxInfrastructureRetries = value }
        | InfrastructureRetryDelaySeconds ->
            { draft with
                InfrastructureRetryDelaySeconds = value }
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
                Int64.TryParse model.Draft.MaxRawTokens,
                Int32.TryParse model.Draft.MaxInfrastructureRetries,
                Double.TryParse model.Draft.InfrastructureRetryDelaySeconds
            with
            | (true, minDelta),
              (true, maxExperiments),
              (true, maxRawTokens),
              (true, maxInfrastructureRetries),
              (true, infrastructureRetryDelaySeconds) ->
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
                          MaxInconclusiveRetries = model.Draft.MaxInconclusiveRetries
                          MaxInfrastructureRetries = maxInfrastructureRetries
                          InfrastructureRetryDelay = TimeSpan.FromSeconds infrastructureRetryDelaySeconds }
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
                      GraphSearch = Defaults.graphSearch
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
            | _ ->
                Error
                    "Min delta, experiment and token budgets, infrastructure retries, and retry delay must be valid numbers."

    let update
        (runtime: HarnessRuntime)
        (pickRepositoryFolder: unit -> Async<Result<string option, string>>)
        (pickDataRootFolder: unit -> Async<Result<string option, string>>)
        (loadExperiment: unit -> Async<Result<(string * HarnessConfig * string option) option, string>>)
        (saveExperiment: string -> HarnessConfig -> Async<Result<string option, string>>)
        (codexExecutable: string)
        (message: Msg)
        (model: Model)
        =
        let loadEvolution runId requestId =
            Cmd.OfAsync.perform (fun () -> runtime.LoadEvolution(runId, CancellationToken.None)) () (fun result ->
                EvolutionLoaded(requestId, runId, result))

        let loadKnowledge runId =
            Cmd.OfAsync.perform
                (fun () -> runtime.LoadRepositoryKnowledge(runId, CancellationToken.None))
                ()
                (fun result -> KnowledgeLoaded(runId, result))

        match message with
        | Navigate page ->
            let command =
                match page with
                | History -> Cmd.batch [ Cmd.ofMsg RefreshHistory; Cmd.ofMsg PollMonitor ]
                | Evolution
                | Knowledge -> Cmd.ofMsg LoadEvolutionRuns
                | CurrentRun
                | Setup -> Cmd.ofMsg PollMonitor
                | Settings -> Cmd.none

            { model with Page = page }, command
        | DecreaseFont ->
            { model with
                FontScale = max 0.8 (model.FontScale - 0.1) },
            Cmd.none
        | IncreaseFont ->
            { model with
                FontScale = min 1.6 (model.FontScale + 0.1) },
            Cmd.none
        | DraftChanged(field, value) ->
            let updatedDraft = updateDraft field value model.Draft

            if updatedDraft = model.Draft then
                model, Cmd.none
            else
                let updated =
                    { model with
                        Draft = updatedDraft
                        Campaign = None
                        AssociatedRun = None
                        ExperimentFile = None
                        Error = None }

                match field with
                | DraftField.SourcePath -> { updated with Repository = None }, Cmd.none
                | _ -> updated, Cmd.none
        | DataRootChanged value ->
            if value = model.DataRoot then
                model, Cmd.none
            else
                { model with
                    DataRoot = value
                    DataRootOverridden = true
                    Campaign = None
                    AssociatedRun = None
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
                    Campaign = None
                    AssociatedRun = None
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
                match runtime.TrySetDataRoot path with
                | Error error -> { model with Error = Some error }, Cmd.none
                | Ok applied ->
                    { model with
                        DataRoot = applied
                        DataRootOverridden = true
                        Campaign = None
                        AssociatedRun = None
                        Error = None },
                    Cmd.ofMsg PollMonitor
            | Ok None -> model, Cmd.none
            | Error error -> { model with Error = Some error }, Cmd.none
        | LoadExperiment ->
            { model with Busy = true; Error = None },
            Cmd.OfAsync.perform (fun () -> loadExperiment ()) () ExperimentLoaded
        | ExperimentLoaded result ->
            match result with
            | Ok(Some(path, config, configuredDataRoot)) ->
                let dataRoot, dataRootError =
                    match configuredDataRoot, model.DataRootOverridden with
                    | Some value, false ->
                        match runtime.TrySetDataRoot value with
                        | Ok applied -> applied, None
                        | Error error -> model.DataRoot, Some error
                    | _ -> model.DataRoot, None

                { model with
                    DataRoot = dataRoot
                    Draft = draftFromConfig config
                    Repository = None
                    CodexHealth = None
                    Campaign = Some(HeadlessCampaign.status path dataRoot)
                    AssociatedRun = None
                    Busy = false
                    ExperimentFile = Some path
                    Error = dataRootError },
                Cmd.ofMsg PollMonitor
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
                Cmd.OfAsync.perform (fun () -> saveExperiment model.DataRoot config) () ExperimentSaved
        | ExperimentSaved result ->
            match result with
            | Ok(Some path) ->
                match runtime.TrySetDataRoot model.DataRoot with
                | Error error ->
                    { model with
                        Busy = false
                        Error = Some error },
                    Cmd.none
                | Ok applied ->
                    { model with
                        DataRoot = applied
                        Busy = false
                        ExperimentFile = Some path
                        Campaign = Some(HeadlessCampaign.status path applied)
                        Error = None },
                    Cmd.ofMsg PollMonitor
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
        | LaunchCampaign ->
            match model.ExperimentFile with
            | None ->
                { model with
                    Error = Some "Load or save a campaign file before launching it." },
                Cmd.none
            | Some path ->
                match runtime.TrySetDataRoot model.DataRoot with
                | Error error -> { model with Error = Some error }, Cmd.none
                | Ok applied ->
                    { model with
                        DataRoot = applied
                        Busy = true
                        Error = None },
                    Cmd.OfAsync.perform
                        (fun () -> async { return HeadlessCampaign.launch path applied codexExecutable })
                        ()
                        CampaignLaunched
        | CampaignLaunched result ->
            match result with
            | Ok status ->
                { model with
                    Page = CurrentRun
                    Campaign = Some status
                    Busy = false
                    Error = None },
                Cmd.ofMsg PollMonitor
            | Error message ->
                { model with
                    Busy = false
                    Error = Some message },
                Cmd.none
        | StopCampaign ->
            match model.ExperimentFile with
            | None ->
                { model with
                    Error = Some "No campaign is loaded." },
                Cmd.none
            | Some path ->
                { model with Busy = true; Error = None },
                Cmd.OfAsync.perform
                    (fun () -> async { return HeadlessCampaign.requestStop path model.DataRoot })
                    ()
                    CampaignStopRequested
        | CampaignStopRequested result ->
            match result with
            | Ok() ->
                { model with
                    Busy = false
                    Error = None },
                Cmd.ofMsg PollMonitor
            | Error message ->
                { model with
                    Busy = false
                    Error = Some message },
                Cmd.none
        | PollMonitor when model.Page = Evolution -> model, Cmd.none
        | PollMonitor ->
            let status =
                model.ExperimentFile
                |> Option.map (fun path -> HeadlessCampaign.status path model.DataRoot)

            let commands =
                [ Cmd.ofMsg (CampaignStatusPolled status)
                  Cmd.OfAsync.perform (fun () -> runtime.ListEvolutionRuns CancellationToken.None) () MonitorRunsLoaded

                  if model.Page = History && not model.Busy then
                      Cmd.ofMsg RefreshHistory ]

            model, Cmd.batch commands
        | CampaignStatusPolled status when status = model.Campaign -> model, Cmd.none
        | CampaignStatusPolled status -> { model with Campaign = status }, Cmd.none
        | MonitorRunsLoaded result ->
            match result with
            | Error _ -> model, Cmd.none
            | Ok runs ->
                let launchCutoff =
                    model.Campaign
                    |> Option.bind _.StartedAt
                    |> Option.map (fun startedAt -> startedAt - TimeSpan.FromMinutes 5.0)

                let associated =
                    runs
                    |> List.filter (fun run ->
                        String.Equals(
                            Path.GetFullPath run.SourcePath,
                            Path.GetFullPath model.Draft.SourcePath,
                            StringComparison.OrdinalIgnoreCase
                        )
                        && (launchCutoff |> Option.forall (fun cutoff -> run.CreatedAt >= cutoff)))
                    |> List.sortByDescending _.CreatedAt
                    |> List.tryHead

                if runs = model.EvolutionRuns && associated = model.AssociatedRun then
                    model, Cmd.none
                else
                    { model with
                        EvolutionRuns = runs
                        AssociatedRun = associated },
                    Cmd.none
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
                        model.AssociatedRun
                        |> Option.map _.Id
                        |> Option.filter (fun id -> runs |> List.exists (fun item -> item.Id = id)))
                    |> Option.orElseWith (fun () -> runs |> List.tryHead |> Option.map _.Id)

                let next =
                    { model with
                        EvolutionRuns = runs
                        SelectedEvolutionRun = selected
                        Evolution = None
                        Knowledge = None
                        SelectedEvolutionNode = None
                        EvolutionBusy = selected.IsSome
                        KnowledgeBusy = selected.IsSome && model.Page = Knowledge
                        Error = None }

                match selected with
                | Some runId ->
                    let requestId = model.EvolutionRequestId + 1

                    { next with
                        EvolutionRequestId = requestId },
                    if model.Page = Knowledge then
                        Cmd.batch [ loadEvolution runId requestId; loadKnowledge runId ]
                    else
                        loadEvolution runId requestId
                | None -> { next with EvolutionBusy = false }, Cmd.none
        | SelectEvolutionRun runId ->
            let requestId = model.EvolutionRequestId + 1

            { model with
                SelectedEvolutionRun = Some runId
                Evolution = None
                Knowledge = None
                SelectedEvolutionNode = None
                EvolutionBusy = true
                KnowledgeBusy = model.Page = Knowledge
                EvolutionRequestId = requestId
                Error = None },
            if model.Page = Knowledge then
                Cmd.batch [ loadEvolution runId requestId; loadKnowledge runId ]
            else
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
        | RefreshKnowledge ->
            match model.SelectedEvolutionRun with
            | None -> model, Cmd.ofMsg LoadEvolutionRuns
            | Some runId -> { model with KnowledgeBusy = true }, loadKnowledge runId
        | KnowledgeLoaded(runId, result) when model.SelectedEvolutionRun = Some runId ->
            match result with
            | Ok graph ->
                { model with
                    Knowledge = Some graph
                    KnowledgeBusy = false
                    Error = None },
                Cmd.none
            | Error error ->
                { model with
                    Knowledge = None
                    KnowledgeBusy = false
                    Error = Some(errorText error) },
                Cmd.none
        | KnowledgeLoaded _ -> model, Cmd.none
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
