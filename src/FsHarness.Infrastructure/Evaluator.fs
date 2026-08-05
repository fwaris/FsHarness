namespace FsHarness.Infrastructure

open System
open System.IO
open System.Text.Json
open System.Threading
open FsHarness.Core

[<RequireQualifiedAccess>]
module Evaluator =
    let private error code summary detail retryable =
        let value =
            HarnessError.create code HarnessErrorCategory.Evaluation summary
            |> HarnessError.withDetail detail

        if retryable then HarnessError.retryable value else value

    let private tryProperty name (element: JsonElement) =
        match element.TryGetProperty(name: string) with
        | true, value -> Some value
        | false, _ -> None

    let private parseResult path =
        try
            use stream = File.OpenRead path
            use document = JsonDocument.Parse stream
            let root = document.RootElement

            let schemaVersion =
                tryProperty "schemaVersion" root
                |> Option.bind (fun value ->
                    match value.TryGetInt32() with
                    | true, number -> Some number
                    | false, _ -> None)

            let constraints =
                match tryProperty "constraints" root with
                | Some values when values.ValueKind = JsonValueKind.Object ->
                    values.EnumerateObject()
                    |> Seq.choose (fun property ->
                        match property.Value.ValueKind with
                        | JsonValueKind.True -> Some(property.Name, true)
                        | JsonValueKind.False -> Some(property.Name, false)
                        | _ -> None)
                    |> Map.ofSeq
                | _ -> Map.empty

            let metrics =
                match tryProperty "metrics" root with
                | Some values when values.ValueKind = JsonValueKind.Object ->
                    values.EnumerateObject()
                    |> Seq.choose (fun property ->
                        match property.Value.TryGetDecimal() with
                        | true, number -> Some(property.Name, number)
                        | false, _ -> None)
                    |> Map.ofSeq
                | _ -> Map.empty

            let summary =
                tryProperty "summary" root
                |> Option.bind (fun value ->
                    if value.ValueKind = JsonValueKind.String then
                        value.GetString() |> Option.ofObj
                    else
                        None)
                |> Option.defaultValue ""

            let status =
                match schemaVersion, tryProperty "status" root with
                | Some 1, _ -> Some EvaluationStatus.Complete
                | Some 2, Some value when value.ValueKind = JsonValueKind.String ->
                    match value.GetString() with
                    | "complete" -> Some EvaluationStatus.Complete
                    | "inconclusive" -> Some EvaluationStatus.Inconclusive
                    | _ -> None
                | _ -> None

            let evidence =
                match tryProperty "evidence" root with
                | Some values when values.ValueKind = JsonValueKind.Array ->
                    values.EnumerateArray()
                    |> Seq.choose (fun value ->
                        if value.ValueKind = JsonValueKind.String then
                            value.GetString() |> Option.ofObj
                        else
                            None)
                    |> List.ofSeq
                | _ -> []

            match schemaVersion, status with
            | Some version, Some parsedStatus when (version = 1 || version = 2) && not (Map.isEmpty metrics) ->
                Ok
                    { SchemaVersion = version
                      Status = parsedStatus
                      Constraints = constraints
                      Metrics = metrics
                      Summary = summary
                      Evidence = evidence }
            | Some version, _ -> Error $"Unsupported evaluator schema {version}, status, or metrics."
            | None, _ -> Error "Evaluator result did not contain an integer schemaVersion."
        with exceptionValue ->
            Error exceptionValue.Message

    let private resolveExecutable (worktree: string) (executable: string) =
        if Path.IsPathRooted executable then
            executable
        elif
            executable.Contains(Path.DirectorySeparatorChar)
            || executable.Contains(Path.AltDirectorySeparatorChar)
        then
            Path.GetFullPath(Path.Combine(worktree, executable))
        else
            executable

    let run (request: EvaluationRequest) (cancellationToken: CancellationToken) =
        async {
            let spec = request.Spec
            let resultPath = request.ResultPath
            let evaluationRoot = Path.GetFullPath request.CandidatePath

            let workingDirectory =
                Path.GetFullPath(Path.Combine(evaluationRoot, spec.WorkingDirectory))

            match DataPaths.ensureContained evaluationRoot workingDirectory with
            | Error detail ->
                return
                    Error(
                        error "evaluator.cwd_outside" "Evaluator working directory escaped its worktree." detail false
                    )
            | Ok safeWorkingDirectory when not (Directory.Exists safeWorkingDirectory) ->
                return
                    Error(
                        error
                            "evaluator.cwd_missing"
                            "Evaluator working directory does not exist."
                            safeWorkingDirectory
                            false
                    )
            | Ok safeWorkingDirectory ->
                try
                    if File.Exists resultPath then
                        File.Delete resultPath

                    Directory.CreateDirectory(Path.GetDirectoryName resultPath) |> ignore

                    let environment =
                        SanitizedEnvironment.core ()
                        |> Map.add "FSHARNESS_RESULT_PATH" resultPath
                        |> Map.add "FSHARNESS_CANDIDATE_PATH" evaluationRoot
                        |> Map.add "FSHARNESS_PARENT_PATH" (Path.GetFullPath request.DerivationParentPath)
                        |> Map.add "FSHARNESS_CHAMPION_PATH" (Path.GetFullPath request.ChampionPath)
                        |> Map.add "FSHARNESS_FRONTIER_PATH" (Path.GetFullPath request.ChampionPath)
                        |> Map.add "GIT_OPTIONAL_LOCKS" "0"
                        |> Map.add "GIT_TERMINAL_PROMPT" "0"

                    let! processResult =
                        ProcessRunner.run
                            { Executable = resolveExecutable evaluationRoot spec.Executable
                              Arguments = spec.Arguments
                              WorkingDirectory = Some safeWorkingDirectory
                              StandardInput = None
                              Environment = environment
                              ClearEnvironment = true
                              Timeout = spec.Timeout
                              MaxCaptureCharacters = 1_000_000 }
                            cancellationToken

                    match processResult with
                    | Error processError ->
                        return
                            Error
                                { processError with
                                    Code = "evaluator.process_failed"
                                    Category = HarnessErrorCategory.Evaluation
                                    Summary = "Evaluator process failed." }
                    | Ok value when value.ExitCode <> 0 ->
                        return
                            Error(
                                error
                                    "evaluator.nonzero_exit"
                                    $"Evaluator exited with code {value.ExitCode}; nonzero means evaluator infrastructure failure."
                                    value.Stderr
                                    true
                            )
                    | Ok _ when not (File.Exists resultPath) ->
                        return
                            Error(
                                error
                                    "evaluator.result_missing"
                                    "Evaluator completed without writing FSHARNESS_RESULT_PATH."
                                    resultPath
                                    true
                            )
                    | Ok _ ->
                        match parseResult resultPath with
                        | Ok result -> return Ok result
                        | Error detail ->
                            return
                                Error(error "evaluator.result_invalid" "Evaluator result JSON is invalid." detail false)
                with exceptionValue ->
                    return Error(error "evaluator.failed" "Evaluator execution failed." exceptionValue.Message true)
        }

    let port = { Run = run }
