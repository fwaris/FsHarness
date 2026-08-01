namespace FsHarness.Core

open System
open System.IO

type EvaluatorSpec =
    { Executable: string
      Arguments: string list
      WorkingDirectory: string
      Timeout: TimeSpan
      RequiredConstraints: string list }

type MetricSpec =
    { Name: string
      Direction: MetricDirection
      MinDelta: decimal
      Target: decimal option }

type ModelSpec = { Id: string; Effort: ReasoningEffort }

type RunBudgets =
    { MaxExperiments: int
      MaxRawTokens: int64
      MaxDuration: TimeSpan
      CodexTimeout: TimeSpan
      MaxConsecutiveNonImprovements: int
      MaxConsecutiveFailures: int }

type HarnessConfig =
    { SchemaVersion: int
      SourcePath: string
      BaseCommit: CommitOid
      Objective: string
      EditablePaths: string list
      Evaluator: EvaluatorSpec
      Metric: MetricSpec
      Model: ModelSpec
      Budgets: RunBudgets
      PromotionMode: PromotionMode }

[<RequireQualifiedAccess>]
module Defaults =
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
    let currentSchemaVersion = 1

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

    let validate config =
        let errors = ResizeArray<string>()

        if config.SchemaVersion <> currentSchemaVersion then
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

        if String.IsNullOrWhiteSpace config.Evaluator.Executable then
            errors.Add "An evaluator executable is required."

        if Path.IsPathRooted config.Evaluator.WorkingDirectory then
            errors.Add "The evaluator working directory must be relative to the evaluation worktree."

        if config.Evaluator.Timeout <= TimeSpan.Zero then
            errors.Add "The evaluator timeout must be positive."

        if String.IsNullOrWhiteSpace config.Metric.Name then
            errors.Add "A primary metric name is required."

        if config.Metric.MinDelta < 0M then
            errors.Add "Metric minDelta cannot be negative."

        if String.IsNullOrWhiteSpace config.Model.Id then
            errors.Add "A model ID is required."

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
