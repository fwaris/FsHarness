namespace FsHarness.Cli

open System
open System.IO
open System.Text.Json
open System.Threading
open FsHarness.Codex
open FsHarness.Core
open FsHarness.Infrastructure

[<RequireQualifiedAccess>]
module Program =
    type private Command =
        | Run
        | Resume
        | Status
        | Children
        | Leaves
        | Lineage
        | Diff
        | Knowledge
        | Plans
        | Health
        | Annotate
        | Annotations

    type private Options =
        { Command: Command
          ConfigPath: string option
          RunId: RunId option
          DataRoot: string option
          CodexPath: string option
          SummaryPath: string option
          Commit: CommitOid option
          FromCommit: CommitOid option
          ToCommit: CommitOid option
          Query: string option
          Limit: int
          Author: string option
          Message: string option
          ExperimentId: ExperimentId option }

    let private usage () =
        eprintfn "Usage:"
        eprintfn "  fsharness run --config <file> [--data-root <dir>] [--codex <exe>] [--summary <file>]"
        eprintfn "  fsharness resume --run-id <guid> [--data-root <dir>] [--codex <exe>] [--summary <file>]"
        eprintfn "  fsharness status [--run-id <guid>] [--data-root <dir>]"
        eprintfn "  fsharness children --run-id <guid> --commit <oid> [--data-root <dir>]"
        eprintfn "  fsharness leaves --run-id <guid> [--data-root <dir>]"
        eprintfn "  fsharness lineage --run-id <guid> --commit <oid> [--data-root <dir>]"
        eprintfn "  fsharness diff --run-id <guid> --from <oid> --to <oid> [--data-root <dir>]"
        eprintfn "  fsharness knowledge --run-id <guid> --query <text> [--limit <count>] [--data-root <dir>]"
        eprintfn "  fsharness plans --run-id <guid> [--data-root <dir>]"
        eprintfn "  fsharness health --run-id <guid> [--data-root <dir>]"
        eprintfn "  fsharness annotate --run-id <guid> --author <name> --message <text> [--experiment-id <guid>]"
        eprintfn "  fsharness annotations --run-id <guid> [--data-root <dir>]"

    let private parseRunId (value: string) =
        match Guid.TryParse value with
        | true, parsed -> Ok(RunId.ofGuid parsed)
        | false, _ -> Error $"Invalid run ID '{value}'."

    let private parseExperimentId (value: string) =
        match Guid.TryParse value with
        | true, parsed -> Ok(ExperimentId.ofGuid parsed)
        | false, _ -> Error $"Invalid experiment ID '{value}'."

    let private parse (arguments: string list) =
        let commandAndRest =
            match arguments with
            | "run" :: rest -> Ok(Command.Run, rest)
            | "resume" :: rest -> Ok(Command.Resume, rest)
            | "status" :: rest -> Ok(Command.Status, rest)
            | "children" :: rest -> Ok(Command.Children, rest)
            | "leaves" :: rest -> Ok(Command.Leaves, rest)
            | "lineage" :: rest -> Ok(Command.Lineage, rest)
            | "diff" :: rest -> Ok(Command.Diff, rest)
            | "knowledge" :: rest -> Ok(Command.Knowledge, rest)
            | "plans" :: rest -> Ok(Command.Plans, rest)
            | "health" :: rest -> Ok(Command.Health, rest)
            | "annotate" :: rest -> Ok(Command.Annotate, rest)
            | "annotations" :: rest -> Ok(Command.Annotations, rest)
            | command :: _ -> Error $"Unknown command '{command}'."
            | [] -> Error "A command is required."

        let rec loop options remaining =
            match remaining with
            | [] -> Ok options
            | "--config" :: value :: rest -> loop { options with ConfigPath = Some value } rest
            | "--run-id" :: value :: rest ->
                parseRunId value
                |> Result.bind (fun runId -> loop { options with RunId = Some runId } rest)
            | "--data-root" :: value :: rest -> loop { options with DataRoot = Some value } rest
            | "--codex" :: value :: rest -> loop { options with CodexPath = Some value } rest
            | "--summary" :: value :: rest ->
                loop
                    { options with
                        SummaryPath = Some value }
                    rest
            | "--commit" :: value :: rest ->
                loop
                    { options with
                        Commit = Some(CommitOid.create value) }
                    rest
            | "--from" :: value :: rest ->
                loop
                    { options with
                        FromCommit = Some(CommitOid.create value) }
                    rest
            | "--to" :: value :: rest ->
                loop
                    { options with
                        ToCommit = Some(CommitOid.create value) }
                    rest
            | "--query" :: value :: rest -> loop { options with Query = Some value } rest
            | "--limit" :: value :: rest ->
                match Int32.TryParse value with
                | true, parsed when parsed > 0 -> loop { options with Limit = parsed } rest
                | _ -> Error $"Invalid positive limit '{value}'."
            | "--author" :: value :: rest -> loop { options with Author = Some value } rest
            | "--message" :: value :: rest -> loop { options with Message = Some value } rest
            | "--experiment-id" :: value :: rest ->
                parseExperimentId value
                |> Result.bind (fun experimentId ->
                    loop
                        { options with
                            ExperimentId = Some experimentId }
                        rest)
            | unknown :: _ -> Error $"Unknown or incomplete argument '{unknown}'."

        commandAndRest
        |> Result.bind (fun (command, rest) ->
            loop
                { Command = command
                  ConfigPath = None
                  RunId = None
                  DataRoot = None
                  CodexPath = None
                  SummaryPath = None
                  Commit = None
                  FromCommit = None
                  ToCommit = None
                  Query = None
                  Limit = 10
                  Author = None
                  Message = None
                  ExperimentId = None }
                rest)
        |> Result.bind (fun options ->
            match options.Command, options.ConfigPath, options.RunId with
            | Command.Run, None, _ -> Error "--config is required for run."
            | Command.Resume, _, None -> Error "--run-id is required for resume."
            | Command.Run, _, Some _ -> Error "--run-id is not valid for run."
            | Command.Resume, Some _, _
            | Command.Status, Some _, _ -> Error "--config is only valid for run."
            | (Command.Children | Command.Leaves | Command.Lineage | Command.Diff), Some _, _ ->
                Error "--config is only valid for run."
            | (Command.Knowledge | Command.Plans | Command.Health | Command.Annotate | Command.Annotations), Some _, _ ->
                Error "--config is only valid for run."
            | (Command.Children | Command.Leaves | Command.Lineage | Command.Diff), _, None ->
                Error "--run-id is required for graph queries."
            | (Command.Knowledge | Command.Plans | Command.Health | Command.Annotate | Command.Annotations), _, None ->
                Error "--run-id is required for this command."
            | (Command.Children | Command.Lineage), _, Some _ when options.Commit.IsNone ->
                Error "--commit is required for this graph query."
            | Command.Diff, _, Some _ when options.FromCommit.IsNone || options.ToCommit.IsNone ->
                Error "--from and --to are required for diff."
            | Command.Knowledge, _, Some _ when options.Query.IsNone -> Error "--query is required for knowledge."
            | Command.Annotate, _, Some _ when options.Author.IsNone || options.Message.IsNone ->
                Error "--author and --message are required for annotate."
            | _ -> Ok options)

    let private describeError (error: HarnessError) =
        match error.Detail with
        | Some detail -> $"{error.Code}: {error.Summary} {detail}"
        | None -> $"{error.Code}: {error.Summary}"

    let private isTerminal (state: RunState) =
        match state.Status with
        | Completed _
        | Paused _
        | RecoveryRequired _ -> true
        | _ -> false

    let private jsonOptions () =
        JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let private writeSummary (path: string) (summary: CampaignSummary) =
        let destination = Path.GetFullPath path
        Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore

        let document =
            {| schemaVersion = summary.SchemaVersion
               runId = summary.RunId
               status = summary.Status
               inputTokens = summary.InputTokens
               cachedInputTokens = summary.CachedInputTokens
               uncachedInputTokens = summary.UncachedInputTokens
               outputTokens = summary.OutputTokens
               reasoningTokens = summary.ReasoningTokens
               rawTokens = summary.RawTokens
               attempts = summary.Attempts
               acceptedCandidates = summary.AcceptedCandidates
               evaluatorRetries = summary.EvaluatorRetries
               duplicateHypotheses = summary.DuplicateHypotheses
               tokensToFirstQualifiedImprovement =
                summary.TokensToFirstQualifiedImprovement
                |> Option.map Nullable
                |> Option.defaultValue (Nullable())
               acceptedPercentImprovement = summary.AcceptedPercentImprovement
               tokensPerAcceptedOnePercentSpeedup =
                summary.TokensPerAcceptedOnePercentSpeedup
                |> Option.map Nullable
                |> Option.defaultValue (Nullable()) |}

        File.WriteAllText(destination, JsonSerializer.Serialize(document, jsonOptions ()))
        destination

    let private executePrepared options (runtime: HarnessRuntime) (report: PreparedRunReport) (config: HarnessConfig) =
        use cancellation =
            new CancellationTokenSource(config.Budgets.MaxDuration + TimeSpan.FromMinutes 5.0)

        match runtime.Start() with
        | Error error ->
            eprintfn "%s" (describeError error)
            4
        | Ok _ ->
            let completed =
                SpinWait.SpinUntil(
                    (fun () -> runtime.State |> Option.exists isTerminal),
                    config.Budgets.MaxDuration + TimeSpan.FromMinutes 2.0
                )

            if not completed then
                runtime.StopNow()
                eprintfn "Campaign did not reach a terminal state before the headless timeout."
                5
            else
                match runtime.CampaignSummary() with
                | Error error ->
                    eprintfn "%s" (describeError error)
                    6
                | Ok summary ->
                    let summaryPath =
                        options.SummaryPath
                        |> Option.defaultValue (Path.Combine(report.DataDirectory, "campaign-summary.json"))

                    printfn "%s" (writeSummary summaryPath summary)

                    match runtime.State |> Option.map _.Status with
                    | Some(RecoveryRequired _)
                    | Some(Paused _) -> 7
                    | _ -> 0

    let private subscribeActivity (runtime: HarnessRuntime) =
        runtime.Activity.Subscribe(fun item -> eprintfn "[%s] %s" (item.Timestamp.ToString("o")) item.Message)

    let private runNew options =
        match options.ConfigPath |> Option.map ConfigFile.read with
        | None -> 2
        | Some(Error errors) ->
            errors |> List.iter (eprintfn "Configuration: %s")
            2
        | Some(Ok config) ->
            let codexResolution = CliDiscovery.resolve options.CodexPath
            let dataRoot = options.DataRoot |> Option.defaultValue (DataPaths.root ())
            use runtime = new HarnessRuntime(dataRoot, codexResolution.Executable)
            use _activity = subscribeActivity runtime

            match runtime.Prepare(config, CancellationToken.None) |> Async.RunSynchronously with
            | Error error ->
                eprintfn "%s" (describeError error)
                3
            | Ok report ->
                eprintfn "Prepared run %s at score %M." (RunId.text report.RunId) report.BaselineScore
                executePrepared options runtime report config

    let private resume options =
        let codexResolution = CliDiscovery.resolve options.CodexPath
        let dataRoot = options.DataRoot |> Option.defaultValue (DataPaths.root ())
        use runtime = new HarnessRuntime(dataRoot, codexResolution.Executable)
        use _activity = subscribeActivity runtime

        match
            runtime.Recover(options.RunId.Value, CancellationToken.None)
            |> Async.RunSynchronously
        with
        | Error error ->
            eprintfn "%s" (describeError error)
            3
        | Ok report ->
            eprintfn "Recovered run %s at score %M." (RunId.text report.RunId) report.BaselineScore
            executePrepared options runtime report runtime.State.Value.Config

    let private status options =
        let dataRoot = options.DataRoot |> Option.defaultValue (DataPaths.root ())

        use runtime =
            new HarnessRuntime(dataRoot, options.CodexPath |> Option.defaultValue "codex")

        match runtime.ListEvolutionRuns(CancellationToken.None) |> Async.RunSynchronously with
        | Error error ->
            eprintfn "%s" (describeError error)
            3
        | Ok runs ->
            let selected =
                match options.RunId with
                | Some runId -> runs |> List.filter (fun run -> run.Id = runId)
                | None -> runs

            let document =
                selected
                |> List.map (fun run ->
                    {| runId = RunId.text run.Id
                       sourcePath = run.SourcePath
                       status = run.Status
                       metric = run.MetricName
                       baselineScore = run.BaselineScore
                       frontierScore = run.FrontierScore
                       attempts = run.AttemptCount
                       accepted = run.AcceptedCount
                       createdAt = run.CreatedAt
                       updatedAt = run.UpdatedAt |})

            printfn "%s" (JsonSerializer.Serialize(document, jsonOptions ()))

            if options.RunId.IsSome && List.isEmpty selected then
                8
            else
                0

    let private graphNodeDocument (node: WorkGraphNode) =
        {| experimentId = node.ExperimentId |> Option.map ExperimentId.text
           commit = CommitOid.value node.Commit
           parent = node.Parent |> Option.map CommitOid.value
           outcome = Evolution.outcomeText node.Outcome
           metric = node.Metric
           sequence = node.Sequence
           label = node.Label |}

    let private queryGraph options =
        let dataRoot = options.DataRoot |> Option.defaultValue (DataPaths.root ())
        use runtime = new HarnessRuntime(dataRoot, "codex")
        let runId = options.RunId.Value

        let result =
            match options.Command with
            | Command.Children ->
                runtime.WorkGraphChildren(runId, options.Commit.Value, CancellationToken.None)
                |> Async.RunSynchronously
                |> Result.map (List.map graphNodeDocument >> JsonSerializer.Serialize)
            | Command.Leaves ->
                runtime.WorkGraphLeaves(runId, CancellationToken.None)
                |> Async.RunSynchronously
                |> Result.map (List.map graphNodeDocument >> JsonSerializer.Serialize)
            | Command.Lineage ->
                runtime.WorkGraphLineage(runId, options.Commit.Value, CancellationToken.None)
                |> Async.RunSynchronously
                |> Result.map (List.map graphNodeDocument >> JsonSerializer.Serialize)
            | Command.Diff ->
                runtime.WorkGraphDiff(runId, options.FromCommit.Value, options.ToCommit.Value, CancellationToken.None)
                |> Async.RunSynchronously
            | _ ->
                Error(
                    HarnessError.create
                        "cli.graph_command_invalid"
                        HarnessErrorCategory.Configuration
                        "The selected command is not a graph query."
                )

        match result with
        | Error error ->
            eprintfn "%s" (describeError error)
            3
        | Ok value ->
            printfn "%s" value
            0

    let private knowledgeValueText value =
        match value with
        | KnowledgeValue.Entity entityId -> KnowledgeEntityId.text entityId
        | KnowledgeValue.Text text -> text
        | KnowledgeValue.Number number -> string number
        | KnowledgeValue.Flag flag -> string flag

    let private annotationTargetText target =
        match target with
        | AnnotationTarget.Run -> "run"
        | AnnotationTarget.Experiment experimentId -> $"experiment:{ExperimentId.text experimentId}"
        | AnnotationTarget.Claim claimId -> $"claim:{KnowledgeClaimId.text claimId}"

    let private runOperational options =
        let dataRoot = options.DataRoot |> Option.defaultValue (DataPaths.root ())
        use runtime = new HarnessRuntime(dataRoot, "codex")
        let runId = options.RunId.Value

        let printDocument document =
            printfn "%s" (JsonSerializer.Serialize(document, jsonOptions ()))
            0

        let printError error =
            eprintfn "%s" (describeError error)
            3

        match options.Command with
        | Command.Knowledge ->
            match
                runtime.SearchKnowledge(runId, options.Query.Value, options.Limit, CancellationToken.None)
                |> Async.RunSynchronously
            with
            | Error error -> printError error
            | Ok hits ->
                hits
                |> List.map (fun hit ->
                    {| claimId = KnowledgeClaimId.text hit.Claim.Id
                       subject = hit.Subject.CanonicalName
                       predicate = hit.Claim.Predicate
                       value = knowledgeValueText hit.Claim.Object
                       confidence = hit.Claim.Confidence
                       score = hit.Score
                       sources = hit.Sources |> List.map _.Location |})
                |> printDocument
        | Command.Plans ->
            match runtime.LoadWorkPlans(runId, CancellationToken.None) |> Async.RunSynchronously with
            | Error error -> printError error
            | Ok plans ->
                plans
                |> List.map (fun plan ->
                    {| planId = WorkPlanId.text plan.Id
                       experimentId = ExperimentId.text plan.ExperimentId
                       objective = plan.Objective
                       status = plan.Status
                       createdAt = plan.CreatedAt
                       updatedAt = plan.UpdatedAt
                       plan = JsonSerializer.Deserialize<JsonElement>(plan.PlanJson) |})
                |> printDocument
        | Command.Health ->
            match runtime.HealthCheck(runId, CancellationToken.None) |> Async.RunSynchronously with
            | Error error -> printError error
            | Ok health ->
                let document =
                    {| runId = RunId.text health.RunId
                       healthy = health.Healthy
                       artifactCount = health.ArtifactCount
                       pendingOperations = health.PendingOperationCount
                       workPlans = health.WorkPlanCount
                       knowledgeClaims = health.KnowledgeClaimCount
                       issues = health.Issues |}

                printDocument document
        | Command.Annotate ->
            let annotation =
                { Id = RunAnnotationId.create ()
                  RunId = runId
                  Target =
                    options.ExperimentId
                    |> Option.map AnnotationTarget.Experiment
                    |> Option.defaultValue AnnotationTarget.Run
                  Author = options.Author.Value
                  Body = options.Message.Value
                  CreatedAt = DateTimeOffset.UtcNow }

            match
                runtime.AddAnnotation(annotation, CancellationToken.None)
                |> Async.RunSynchronously
            with
            | Error error -> printError error
            | Ok saved ->
                {| annotationId = RunAnnotationId.text saved.Id
                   runId = RunId.text saved.RunId
                   target = annotationTargetText saved.Target
                   author = saved.Author
                   body = saved.Body
                   createdAt = saved.CreatedAt |}
                |> printDocument
        | Command.Annotations ->
            match runtime.LoadAnnotations(runId, CancellationToken.None) |> Async.RunSynchronously with
            | Error error -> printError error
            | Ok annotations ->
                annotations
                |> List.map (fun annotation ->
                    {| annotationId = RunAnnotationId.text annotation.Id
                       target = annotationTargetText annotation.Target
                       author = annotation.Author
                       body = annotation.Body
                       createdAt = annotation.CreatedAt |})
                |> printDocument
        | _ -> 1

    [<EntryPoint>]
    let main arguments =
        match parse (List.ofArray arguments) with
        | Error error ->
            eprintfn "%s" error
            usage ()
            1
        | Ok options ->
            match options.Command with
            | Command.Run -> runNew options
            | Command.Resume -> resume options
            | Command.Status -> status options
            | Command.Children
            | Command.Leaves
            | Command.Lineage
            | Command.Diff -> queryGraph options
            | Command.Knowledge
            | Command.Plans
            | Command.Health
            | Command.Annotate
            | Command.Annotations -> runOperational options
