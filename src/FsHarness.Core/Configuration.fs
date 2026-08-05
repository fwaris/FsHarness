namespace FsHarness.Core

open System
open System.IO

type EvaluatorSpec =
    { Executable: string
      Arguments: string list
      WorkingDirectory: string
      Timeout: TimeSpan
      RequiredConstraints: string list
      MaxInconclusiveRetries: int
      MaxInfrastructureRetries: int
      InfrastructureRetryDelay: TimeSpan }

type MetricComparison =
    | RetainedScore
    | EvaluationMetric of string

type MetricSpec =
    { Name: string
      Direction: MetricDirection
      MinDelta: decimal
      Target: decimal option
      Comparison: MetricComparison }

type ModelSpec = { Id: string; Effort: ReasoningEffort }

type PromptProfile =
    { MaxMemoryCount: int
      MaxMemoryCharacters: int
      MaxEvaluationFindings: int
      MaxEvaluationCharacters: int }

type RunBudgets =
    { MaxExperiments: int
      MaxRawTokens: int64
      MaxDuration: TimeSpan
      CodexTimeout: TimeSpan
      MaxConsecutiveNonImprovements: int
      MaxConsecutiveFailures: int }

type GraphSearchSpec =
    { InitialFanOut: int
      BeamWidth: int
      ExpansionsPerHeadPerRound: int
      OrdinaryCandidatesPerSynthesis: int
      StagnationTrigger: int
      MaxSynthesisBudgetFraction: decimal
      MaxSynthesisDepth: int
      ConflictResolutionAttempts: int
      MaxConflictFiles: int
      MaxConflictCharacters: int }

type HarnessConfig =
    { SchemaVersion: int
      SourcePath: string
      BaseCommit: CommitOid
      Objective: string
      EditablePaths: string list
      SeedPatches: string list
      Evaluator: EvaluatorSpec
      Metric: MetricSpec
      Model: ModelSpec
      PromptProfile: PromptProfile
      GraphSearch: GraphSearchSpec
      Budgets: RunBudgets
      PromotionMode: PromotionMode }

[<RequireQualifiedAccess>]
module Defaults =
    let evaluatorMaxInfrastructureRetries = 120
    let evaluatorInfrastructureRetryDelay = TimeSpan.FromSeconds 15.0

    let promptProfile =
        { MaxMemoryCount = 5
          MaxMemoryCharacters = 6_000
          MaxEvaluationFindings = Int32.MaxValue
          MaxEvaluationCharacters = 2_000 }

    let graphSearch =
        { InitialFanOut = 2
          BeamWidth = 4
          ExpansionsPerHeadPerRound = 1
          OrdinaryCandidatesPerSynthesis = 3
          StagnationTrigger = 3
          MaxSynthesisBudgetFraction = 0.25M
          MaxSynthesisDepth = 2
          ConflictResolutionAttempts = 1
          MaxConflictFiles = 8
          MaxConflictCharacters = 50_000 }

    let budgets =
        { MaxExperiments = 10
          MaxRawTokens = 200_000L
          MaxDuration = TimeSpan.FromHours(2.0)
          CodexTimeout = TimeSpan.FromMinutes(20.0)
          MaxConsecutiveNonImprovements = 5
          MaxConsecutiveFailures = 3 }

    let model =
        { Id = "gpt-5.6-luna"
          Effort = ReasoningEffort.Max }

[<RequireQualifiedAccess>]
module HarnessConfig =
    let currentSchemaVersion = 4

    let private validateEditablePath (path: string) =
        let normalized = path.Replace('\\', '/')

        if String.IsNullOrWhiteSpace normalized then
            Error "Editable paths cannot be empty."
        elif Path.IsPathRooted normalized then
            Error $"Editable path '{path}' must be relative."
        elif normalized.Split('/') |> Array.contains ".." then
            Error $"Editable path '{path}' cannot traverse outside the repository."
        elif normalized = ".git" || normalized.StartsWith(".git/", StringComparison.Ordinal) then
            Error "Git metadata can never be editable."
        elif
            normalized = ".fsharness"
            || normalized.StartsWith(".fsharness/", StringComparison.Ordinal)
        then
            Error "FsHarness control files can never be editable."
        else
            Ok normalized

    let private validateSeedPatch (path: string) =
        let normalized = path.Replace('\\', '/')

        if String.IsNullOrWhiteSpace normalized then
            Error "Seed patch paths cannot be empty."
        elif Path.IsPathRooted normalized || normalized.Split('/') |> Array.contains ".." then
            Error $"Seed patch '{path}' must be a repository-relative path."
        elif not (normalized.StartsWith(".fsharness/seeds/", StringComparison.Ordinal)) then
            Error $"Seed patch '{path}' must be stored below .fsharness/seeds/."
        elif not (normalized.EndsWith(".patch", StringComparison.OrdinalIgnoreCase)) then
            Error $"Seed patch '{path}' must use the .patch extension."
        else
            Ok normalized

    let validate config =
        let errors = ResizeArray<string>()

        if config.SchemaVersion < 1 || config.SchemaVersion > currentSchemaVersion then
            errors.Add $"Unsupported configuration schema {config.SchemaVersion}."

        if String.IsNullOrWhiteSpace config.SourcePath then
            errors.Add "A source repository is required."

        if String.IsNullOrWhiteSpace config.Objective then
            errors.Add "An objective is required."

        if List.isEmpty config.EditablePaths then
            errors.Add "At least one editable path is required."

        config.EditablePaths
        |> List.iter (fun path ->
            match validateEditablePath path with
            | Ok _ -> ()
            | Error error -> errors.Add error)

        config.SeedPatches
        |> List.iter (fun path ->
            match validateSeedPatch path with
            | Ok _ -> ()
            | Error error -> errors.Add error)

        if String.IsNullOrWhiteSpace config.Evaluator.Executable then
            errors.Add "An evaluator executable is required."

        if Path.IsPathRooted config.Evaluator.WorkingDirectory then
            errors.Add "The evaluator working directory must be relative to the evaluation worktree."

        if config.Evaluator.Timeout <= TimeSpan.Zero then
            errors.Add "The evaluator timeout must be positive."

        if config.Evaluator.MaxInconclusiveRetries < 0 then
            errors.Add "Evaluator inconclusive retries cannot be negative."

        if config.Evaluator.MaxInfrastructureRetries < 0 then
            errors.Add "Evaluator infrastructure retries cannot be negative."

        if config.Evaluator.InfrastructureRetryDelay <= TimeSpan.Zero then
            errors.Add "Evaluator infrastructure retry delay must be positive."

        if String.IsNullOrWhiteSpace config.Metric.Name then
            errors.Add "A primary metric name is required."

        match config.Metric.Comparison with
        | EvaluationMetric name when String.IsNullOrWhiteSpace name ->
            errors.Add "The evaluator-provided comparison metric cannot be empty."
        | _ -> ()

        if config.Metric.MinDelta < 0M then
            errors.Add "Metric minDelta cannot be negative."

        if String.IsNullOrWhiteSpace config.Model.Id then
            errors.Add "A model ID is required."

        if
            config.PromptProfile.MaxMemoryCount <= 0
            || config.PromptProfile.MaxMemoryCharacters <= 0
            || config.PromptProfile.MaxEvaluationFindings <= 0
            || config.PromptProfile.MaxEvaluationCharacters <= 0
        then
            errors.Add "Prompt-profile limits must be positive."

        if config.GraphSearch.InitialFanOut <= 0 then
            errors.Add "Graph-search initial fan-out must be positive."

        if config.GraphSearch.BeamWidth <= 0 then
            errors.Add "Graph-search beam width must be positive."

        if config.GraphSearch.ExpansionsPerHeadPerRound <= 0 then
            errors.Add "Graph-search expansions per head must be positive."

        if config.GraphSearch.OrdinaryCandidatesPerSynthesis <= 0 then
            errors.Add "Graph-search synthesis cadence must be positive."

        if config.GraphSearch.StagnationTrigger <= 0 then
            errors.Add "Graph-search stagnation trigger must be positive."

        if
            config.GraphSearch.MaxSynthesisBudgetFraction < 0M
            || config.GraphSearch.MaxSynthesisBudgetFraction > 1M
        then
            errors.Add "Graph-search synthesis budget fraction must be between zero and one."

        if config.GraphSearch.MaxSynthesisDepth < 0 then
            errors.Add "Graph-search synthesis depth cannot be negative."

        if
            config.GraphSearch.ConflictResolutionAttempts < 0
            || config.GraphSearch.MaxConflictFiles < 0
            || config.GraphSearch.MaxConflictCharacters < 0
        then
            errors.Add "Graph-search conflict limits cannot be negative."

        if config.Budgets.MaxExperiments <= 0 then
            errors.Add "The experiment budget must be positive."

        if config.Budgets.MaxRawTokens <= 0L then
            errors.Add "The raw-token budget must be positive."

        if
            config.Budgets.MaxDuration <= TimeSpan.Zero
            || config.Budgets.CodexTimeout <= TimeSpan.Zero
        then
            errors.Add "Time budgets must be positive."

        if config.Budgets.MaxConsecutiveFailures <= 0 then
            errors.Add "The consecutive-failure limit must be positive."

        if config.Budgets.MaxConsecutiveNonImprovements <= 0 then
            errors.Add "The non-improvement limit must be positive."

        if errors.Count = 0 then
            Ok config
        else
            Error(List.ofSeq errors)
