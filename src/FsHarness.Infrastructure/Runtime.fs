namespace FsHarness.Infrastructure

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsHarness.Codex
open FsHarness.Core

type PreparedRunReport =
    { RunId: RunId
      Repository: RepositoryInspection
      Codex: CodexPreflight
      Baseline: EvaluationResult
      BaselineScore: decimal
      DataDirectory: string }

type RuntimeActivity =
    { Timestamp: DateTimeOffset
      ExperimentId: ExperimentId option
      Message: string }

type CampaignSummary =
    { SchemaVersion: int
      RunId: string
      Status: string
      InputTokens: int64
      CachedInputTokens: int64
      UncachedInputTokens: int64
      OutputTokens: int64
      ReasoningTokens: int64
      RawTokens: int64
      Attempts: int
      AcceptedCandidates: int
      EvaluatorRetries: int
      DuplicateHypotheses: int
      TokensToFirstQualifiedImprovement: int64 option
      AcceptedPercentImprovement: decimal
      TokensPerAcceptedOnePercentSpeedup: decimal option }

type HarnessRuntime(dataRoot: string, codexExecutable: string) =
    let root = Path.GetFullPath dataRoot
    let gitStore = GitStore.create root
    let git = GitStore.port gitStore
    let sqlite = SqliteStore.create (Path.Combine(root, "fsharness.db"))
    let journal = SqliteStore.journalPort sqlite
    let memory = SqliteStore.memoryPort sqlite
    let evaluator = Evaluator.port
    let codex = Cli.port
    let stateChanged = Event<RunState>()
    let activity = Event<RuntimeActivity>()
    let evolutionChanged = Event<RunId>()
    let stateGate = obj ()
    let mutable state: RunState option = None
    let mutable prepared: PreparedRunReport option = None
    let mutable runCancellation: CancellationTokenSource option = None
    let mutable activeLock: IDisposable option = None
    let mutable worker: Task option = None
    let mutable evaluatorRetryCount = 0

    let publish message experimentId =
        activity.Trigger
            { Timestamp = DateTimeOffset.UtcNow
              ExperimentId = experimentId
              Message = message }

    let statusText status =
        match status with
        | Ready -> "Ready"
        | Running -> "Running"
        | PauseAfterCurrent -> "PauseAfterCurrent"
        | Paused _ -> "Paused"
        | Stopping -> "Stopping"
        | Completed _ -> "Completed"
        | RecoveryRequired _ -> "RecoveryRequired"

    let persistStatus (next: RunState) =
        SqliteStore.updateRunStatus sqlite next.Id (statusText next.Status) CancellationToken.None
        |> Async.RunSynchronously
        |> ignore

    let notifyEvolution runId = evolutionChanged.Trigger runId

    let persistExperimentStart runId experimentId sequence parent outcome cancellationToken =
        async {
            match! SqliteStore.beginExperiment sqlite runId experimentId sequence parent outcome cancellationToken with
            | Ok() -> notifyEvolution runId
            | Error error -> publish $"Lineage journal warning: {error.Summary}" (Some experimentId)
        }

    let persistCandidate runId experimentId candidate cancellationToken =
        async {
            match! SqliteStore.updateExperimentCandidate sqlite experimentId candidate cancellationToken with
            | Ok() -> notifyEvolution runId
            | Error error -> publish $"Lineage journal warning: {error.Summary}" (Some experimentId)
        }

    let outcomeCode kind payload =
        match kind with
        | "Accepted"
        | "AcceptedAfterReview"
        | "SeedAccepted" -> Some "Accepted"
        | "Rejected" -> Some $"Rejected: {payload}"
        | "RejectedByUser" -> Some $"RejectedByUser: {payload}"
        | "SeedRejected" -> Some $"Rejected: {payload}"
        | "ProtectedPathRejected" -> Some $"RejectedProtected: {payload}"
        | "EvaluationInconclusive" -> Some $"Inconclusive: {payload}"
        | "SeedInconclusive" -> Some $"Inconclusive: {payload}"
        | "Cancelled" -> Some $"Cancelled: {payload}"
        | "PrepareFailed"
        | "SeedPrepareFailed"
        | "SeedApplyFailed"
        | "SeedCaptureFailed"
        | "SeedEvaluationFailed"
        | "CodexFailed"
        | "CaptureFailed"
        | "EvaluationFailed"
        | "AcceptFailed" -> Some $"Failed: {payload}"
        | "SeedProtectedRejected" -> Some $"RejectedProtected: {payload}"
        | "AcceptPending" -> Some "AwaitingReview"
        | _ -> None

    let releaseRunLock () =
        activeLock |> Option.iter _.Dispose()
        activeLock <- None

    let setState next =
        lock stateGate (fun () -> state <- Some next)
        persistStatus next
        stateChanged.Trigger next

    let dispatch event =
        let result =
            lock stateGate (fun () ->
                match state with
                | None -> None
                | Some current ->
                    let next, effects = RunState.transition DateTimeOffset.UtcNow event current
                    state <- Some next
                    Some(next, effects))

        result
        |> Option.iter (fun (next, _) ->
            persistStatus next
            stateChanged.Trigger next)

        result

    let journalEvent runId experimentId kind payload cancellationToken =
        async {
            match! journal.AppendEvent runId experimentId kind payload cancellationToken with
            | Ok() ->
                match experimentId, outcomeCode kind payload with
                | Some id, Some outcome ->
                    match! SqliteStore.completeExperiment sqlite id outcome cancellationToken with
                    | Ok() -> ()
                    | Error error -> publish $"Lineage journal warning: {error.Summary}" (Some id)
                | _ -> ()

                notifyEvolution runId
                return ()
            | Error error -> publish $"Journal warning: {error.Summary}" experimentId
        }

    let recordArtifact runId experimentId kind path cancellationToken =
        async {
            match! SqliteStore.saveArtifact sqlite runId experimentId kind path cancellationToken with
            | Ok() -> return ()
            | Error error -> publish $"Artifact journal warning: {error.Summary}" experimentId
        }

    let experimentSchema =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "hypothesis": { "type": "string", "minLength": 1 },
            "changeSummary": { "type": "string", "minLength": 1 },
            "expectedEffect": { "type": "string", "minLength": 1 },
            "validationNotes": { "type": "array", "items": { "type": "string" } },
            "reusableLesson": { "type": "string", "minLength": 1 }
          },
          "required": ["hypothesis", "changeSummary", "expectedEffect", "validationNotes", "reusableLesson"]
        }
        """

    let validateModel (config: HarnessConfig) (preflight: CodexPreflight) =
        match preflight.Models |> List.tryFind (fun model -> model.Id = config.Model.Id) with
        | None ->
            Error(
                HarnessError.create
                    "codex.model_unavailable"
                    HarnessErrorCategory.Codex
                    $"Model '{config.Model.Id}' is not in the installed Codex catalog."
            )
        | Some model when not (model.SupportedReasoningEfforts |> List.contains config.Model.Effort) ->
            Error(
                HarnessError.create
                    "codex.effort_unavailable"
                    HarnessErrorCategory.Codex
                    $"Model '{config.Model.Id}' does not advertise reasoning effort '{ReasoningEffort.toConfigValue config.Model.Effort}'."
            )
        | Some _ -> Ok()

    let tryCurrent () = lock stateGate (fun () -> state)

    let captureAfterFailure (currentState: RunState) (workspace: CandidateWorkspace) cancellationToken =
        async {
            match!
                git.CaptureCandidate currentState.Id workspace currentState.Config.EditablePaths cancellationToken
            with
            | Ok snapshot ->
                do! persistCandidate currentState.Id workspace.ExperimentId snapshot.Commit cancellationToken
                publish $"Preserved failed candidate {CommitOid.value snapshot.Commit}." (Some workspace.ExperimentId)
            | Error error ->
                publish $"Could not preserve failed candidate: {error.Summary}" (Some workspace.ExperimentId)
        }

    let saveMemory runId experimentId outcome metricValue summary cancellationToken =
        memory.Append
            runId
            { ExperimentId = experimentId
              Outcome = outcome
              Metric = metricValue
              Summary = summary }
            cancellationToken

    let runEvaluatorWithRetries (spec: EvaluatorSpec) frontierPath candidatePath resultPath cancellationToken =
        let rec loop attempt =
            async {
                let! result = evaluator.Run spec frontierPath candidatePath resultPath cancellationToken

                match result with
                | Ok evaluation when
                    evaluation.Status = EvaluationStatus.Inconclusive
                    && attempt < spec.MaxInconclusiveRetries
                    ->
                    evaluatorRetryCount <- evaluatorRetryCount + 1

                    publish
                        $"Evaluator was inconclusive; retrying the same candidate ({attempt + 1}/{spec.MaxInconclusiveRetries})."
                        None

                    return! loop (attempt + 1)
                | _ -> return result
            }

        loop 0

    let evaluateSeedPatches runId (config: HarnessConfig) initialFrontier initialScore cancellationToken =
        let rec loop index frontier score remaining =
            async {
                match remaining with
                | [] -> return Ok(frontier, score)
                | patchPath :: rest ->
                    let experimentId = ExperimentId.create ()

                    do!
                        persistExperimentStart
                            runId
                            experimentId
                            (index + 1)
                            (Some frontier)
                            "SeedActive"
                            cancellationToken

                    publish
                        $"Evaluating protected seed candidate '{patchPath}' at zero Codex-token cost."
                        (Some experimentId)

                    match! git.PrepareCandidate runId experimentId frontier cancellationToken with
                    | Error error ->
                        do! journalEvent runId (Some experimentId) "SeedPrepareFailed" error.Summary cancellationToken
                        return Error error
                    | Ok workspace ->
                        match! git.ApplySeedPatch workspace patchPath cancellationToken with
                        | Error error ->
                            do! journalEvent runId (Some experimentId) "SeedApplyFailed" error.Summary cancellationToken
                            return Error error
                        | Ok() ->
                            match! git.CaptureCandidate runId workspace config.EditablePaths cancellationToken with
                            | Error error ->
                                do!
                                    journalEvent
                                        runId
                                        (Some experimentId)
                                        "SeedCaptureFailed"
                                        error.Summary
                                        cancellationToken

                                return Error error
                            | Ok snapshot when not (List.isEmpty snapshot.ProtectedPaths) ->
                                do! persistCandidate runId experimentId snapshot.Commit cancellationToken

                                do!
                                    journalEvent
                                        runId
                                        (Some experimentId)
                                        "SeedProtectedRejected"
                                        (String.concat ", " snapshot.ProtectedPaths)
                                        cancellationToken

                                return
                                    Error(
                                        HarnessError.create
                                            "git.seed_protected_path"
                                            HarnessErrorCategory.Git
                                            "A protected seed patch modified files outside the configured editable paths."
                                        |> HarnessError.withDetail (String.concat ", " snapshot.ProtectedPaths)
                                    )
                            | Ok snapshot ->
                                let resultPath =
                                    Path.Combine(
                                        DataPaths.artifacts root runId,
                                        "seeds",
                                        $"{index:D2}",
                                        "evaluation.json"
                                    )

                                match!
                                    runEvaluatorWithRetries
                                        config.Evaluator
                                        snapshot.FrontierEvaluationPath
                                        snapshot.EvaluationPath
                                        resultPath
                                        cancellationToken
                                with
                                | Error error ->
                                    do!
                                        journalEvent
                                            runId
                                            (Some experimentId)
                                            "SeedEvaluationFailed"
                                            error.Summary
                                            cancellationToken

                                    return Error error
                                | Ok evaluation ->
                                    do! persistCandidate runId experimentId snapshot.Commit cancellationToken

                                    do!
                                        recordArtifact
                                            runId
                                            (Some experimentId)
                                            "seed-evaluation"
                                            resultPath
                                            cancellationToken

                                    match!
                                        SqliteStore.saveEvaluation
                                            sqlite
                                            runId
                                            experimentId
                                            evaluation
                                            cancellationToken
                                    with
                                    | Error error ->
                                        publish $"Seed journal warning: {error.Summary}" (Some experimentId)
                                    | Ok() -> ()

                                    match evaluation.Status with
                                    | EvaluationStatus.Inconclusive ->
                                        do!
                                            journalEvent
                                                runId
                                                (Some experimentId)
                                                "SeedInconclusive"
                                                patchPath
                                                cancellationToken

                                        return! loop (index + 1) frontier score rest
                                    | EvaluationStatus.Complete ->
                                        match
                                            Evaluation.decide
                                                config.Metric
                                                config.Evaluator.RequiredConstraints
                                                score
                                                evaluation
                                        with
                                        | CandidateDecision.Rejected reason ->
                                            do!
                                                journalEvent
                                                    runId
                                                    (Some experimentId)
                                                    "SeedRejected"
                                                    (string reason)
                                                    cancellationToken

                                            return! loop (index + 1) frontier score rest
                                        | StrictImprovement candidateScore ->
                                            match!
                                                git.AdvanceFrontier runId frontier snapshot.Commit cancellationToken
                                            with
                                            | Error error -> return Error error
                                            | Ok() ->
                                                do!
                                                    journalEvent
                                                        runId
                                                        (Some experimentId)
                                                        "SeedAccepted"
                                                        (string candidateScore)
                                                        cancellationToken

                                                publish
                                                    $"Accepted protected seed candidate at score {candidateScore}."
                                                    (Some experimentId)

                                                return! loop (index + 1) snapshot.Commit candidateScore rest
            }

        loop 0 initialFrontier initialScore config.SeedPatches

    let parseRunConfig (configJson: string option) =
        match configJson with
        | None -> "primary", Maximize, 0
        | Some json ->
            try
                use document = JsonDocument.Parse json
                let root = document.RootElement
                let metric = root.GetProperty "metric"
                let name = metric.GetProperty("name").GetString()

                let direction =
                    match metric.GetProperty("direction").GetString().ToLowerInvariant() with
                    | "minimize" -> Minimize
                    | _ -> Maximize

                let seedCount =
                    match root.TryGetProperty "seedPatches" with
                    | true, value when value.ValueKind = JsonValueKind.Array -> value.GetArrayLength()
                    | _ -> 0

                name, direction, seedCount
            with _ ->
                "primary", Maximize, 0

    let parseEvaluation metricName (json: string) =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            let metrics =
                match root.TryGetProperty "metrics" with
                | true, value when value.ValueKind = JsonValueKind.Object ->
                    value.EnumerateObject()
                    |> Seq.choose (fun property ->
                        match property.Value.TryGetDecimal() with
                        | true, number -> Some(property.Name, number)
                        | false, _ -> None)
                    |> Map.ofSeq
                | _ -> Map.empty

            let summary =
                match root.TryGetProperty "summary" with
                | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
                | _ -> None

            Map.tryFind metricName metrics, summary
        with _ ->
            None, None

    let payloadReason (payload: string) =
        match payload.IndexOf(':') with
        | index when index >= 0 -> payload.Substring(index + 1).Trim()
        | _ -> payload

    let outcomeFromCode (code: string) =
        if code = "Baseline" then
            EvolutionOutcome.Unknown "Baseline"
        elif code = "Active" || code = "SeedActive" then
            EvolutionOutcome.Active "Planned"
        elif code = "AwaitingReview" then
            EvolutionOutcome.Active "Awaiting review"
        elif code = "Accepted" then
            EvolutionOutcome.Accepted
        elif code.StartsWith("RejectedProtected", StringComparison.Ordinal) then
            EvolutionOutcome.Rejected(payloadReason code)
        elif
            code.StartsWith("RejectedByUser", StringComparison.Ordinal)
            || code.StartsWith("Rejected", StringComparison.Ordinal)
        then
            EvolutionOutcome.Rejected(payloadReason code)
        elif code.StartsWith("Failed", StringComparison.Ordinal) then
            EvolutionOutcome.Failed(payloadReason code)
        elif code.StartsWith("Inconclusive", StringComparison.Ordinal) then
            EvolutionOutcome.Inconclusive(payloadReason code)
        elif code.StartsWith("Cancelled", StringComparison.Ordinal) then
            EvolutionOutcome.Cancelled
        else
            EvolutionOutcome.Unknown code

    let outcomeFromEvents (events: HistoryEvent list) =
        events
        |> List.rev
        |> List.tryPick (fun event ->
            match event.Kind with
            | "Accepted"
            | "AcceptedAfterReview"
            | "SeedAccepted" -> Some EvolutionOutcome.Accepted
            | "Rejected"
            | "RejectedByUser"
            | "SeedRejected"
            | "ProtectedPathRejected"
            | "SeedProtectedRejected" -> Some(EvolutionOutcome.Rejected event.Payload)
            | "EvaluationInconclusive"
            | "SeedInconclusive" -> Some(EvolutionOutcome.Inconclusive event.Payload)
            | "Cancelled" -> Some EvolutionOutcome.Cancelled
            | "PrepareFailed"
            | "SeedPrepareFailed"
            | "SeedApplyFailed"
            | "SeedCaptureFailed"
            | "SeedEvaluationFailed"
            | "CodexFailed"
            | "CaptureFailed"
            | "EvaluationFailed"
            | "AcceptFailed" -> Some(EvolutionOutcome.Failed event.Payload)
            | "AcceptPending" -> Some(EvolutionOutcome.Active "Awaiting review")
            | "CandidatePlanned" -> Some(EvolutionOutcome.Active "Planned")
            | _ -> None)

    let parentFromEvents (events: HistoryEvent list) =
        events
        |> List.tryPick (fun event ->
            if event.Kind = "CandidatePlanned" && event.Payload.Length >= 40 then
                Some(CommitOid.create (event.Payload.Substring(0, 40)))
            else
                None)

    let loadEvolutionSnapshot (storedRun: StoredRun) cancellationToken =
        async {
            let metricName, direction, seedCount = parseRunConfig storedRun.ConfigJson

            let storedExperiments, experimentWarnings =
                match SqliteStore.loadExperiments sqlite storedRun.Id with
                | Ok value -> value, []
                | Error error -> [], [ error.Summary ]

            let evaluations, evaluationWarnings =
                match SqliteStore.loadEvaluationsForRun sqlite storedRun.Id with
                | Ok value -> value, []
                | Error error -> [], [ error.Summary ]

            let memories, memoryWarnings =
                match SqliteStore.loadEvolutionMemories sqlite storedRun.Id with
                | Ok value -> value, []
                | Error error -> [], [ error.Summary ]

            let usages, usageWarnings =
                match SqliteStore.loadUsageForRun sqlite storedRun.Id with
                | Ok value -> value, []
                | Error error -> [], [ error.Summary ]

            let events, eventWarnings =
                match SqliteStore.loadEvents sqlite 100_000 with
                | Ok value -> value |> List.filter (fun item -> item.RunId = storedRun.Id), []
                | Error error -> [], [ error.Summary ]

            let! lineageResult = GitStore.loadLineage gitStore storedRun.Id cancellationToken

            let lineage, lineageWarnings =
                match lineageResult with
                | Ok value -> Some value, []
                | Error error -> None, [ error.Summary ]

            let eventGroups =
                events
                |> List.sortBy _.CreatedAt
                |> List.choose (fun event -> event.ExperimentId |> Option.map (fun id -> id, event))
                |> List.groupBy fst
                |> List.map (fun (id, values) -> id, values |> List.map snd)
                |> Map.ofList

            let gitCandidates =
                lineage |> Option.map _.Candidates |> Option.defaultValue Map.empty

            let storedById =
                storedExperiments |> List.map (fun value -> value.Id, value) |> Map.ofList

            let allExperimentIds =
                Set.union (storedById |> Map.keys |> Set.ofSeq) (eventGroups |> Map.keys |> Set.ofSeq)
                |> Set.toList

            let fallbackSequence id =
                allExperimentIds |> List.findIndex ((=) id) |> (+) 1

            let records =
                allExperimentIds
                |> List.map (fun id ->
                    match Map.tryFind id storedById with
                    | Some stored -> stored
                    | None ->
                        let group = Map.tryFind id eventGroups |> Option.defaultValue []

                        let candidate, gitParent =
                            Map.tryFind id gitCandidates
                            |> Option.map (fun (commit, parent) -> Some commit, parent)
                            |> Option.defaultValue (None, None)

                        { Id = id
                          RunId = storedRun.Id
                          Sequence = seedCount + fallbackSequence id
                          Parent = parentFromEvents group |> Option.orElse gitParent
                          Candidate = candidate
                          Outcome =
                            group
                            |> outcomeFromEvents
                            |> Option.map Evolution.outcomeText
                            |> Option.defaultValue "Unknown"
                          CreatedAt =
                            group
                            |> List.tryHead
                            |> Option.map _.CreatedAt
                            |> Option.defaultValue storedRun.CreatedAt
                          UpdatedAt =
                            group
                            |> List.tryLast
                            |> Option.map _.CreatedAt
                            |> Option.defaultValue storedRun.UpdatedAt })

            let evaluationsById =
                evaluations
                |> List.sortBy _.CreatedAt
                |> List.fold
                    (fun state value ->
                        let metric, summary = parseEvaluation metricName value.ResultJson
                        Map.add value.ExperimentId (metric, summary) state)
                    Map.empty

            let memoriesById =
                memories |> List.map (fun value -> value.ExperimentId, value) |> Map.ofList

            let usagesById =
                usages |> List.map (fun value -> value.ExperimentId, value.Usage) |> Map.ofList

            let baselineRecord =
                storedExperiments |> List.tryFind (fun value -> value.Outcome = "Baseline")

            let baselineEvaluation =
                match baselineRecord with
                | Some value -> Map.tryFind value.Id evaluationsById
                | None ->
                    evaluations
                    |> List.sortBy _.CreatedAt
                    |> List.tryFind (fun value -> not (Set.contains value.ExperimentId (Set.ofList allExperimentIds)))
                    |> Option.bind (fun value -> Map.tryFind value.ExperimentId evaluationsById)

            let baselineCommit =
                lineage
                |> Option.bind _.Baseline
                |> Option.orElseWith (fun () ->
                    storedRun.ConfigJson
                    |> Option.bind (fun json ->
                        try
                            use document = JsonDocument.Parse json
                            Some(CommitOid.create (document.RootElement.GetProperty("baseCommit").GetString()))
                        with _ ->
                            None))

            let baselineId = baselineRecord |> Option.map (fun value -> value.Id)

            let nodes =
                records
                |> List.filter (fun value -> baselineId <> Some value.Id)
                |> List.sortBy _.Sequence
                |> List.map (fun record ->
                    let group = Map.tryFind record.Id eventGroups |> Option.defaultValue []
                    let eventOutcome = outcomeFromEvents group

                    let outcome =
                        eventOutcome
                        |> Option.orElseWith (fun () -> Some(outcomeFromCode record.Outcome))
                        |> Option.defaultValue (EvolutionOutcome.Unknown record.Outcome)

                    let candidate =
                        record.Candidate
                        |> Option.orElseWith (fun () -> Map.tryFind record.Id gitCandidates |> Option.map fst)

                    let parent =
                        record.Parent
                        |> Option.orElseWith (fun () -> Map.tryFind record.Id gitCandidates |> Option.bind snd)

                    let metric, evaluationSummary =
                        Map.tryFind record.Id evaluationsById |> Option.defaultValue (None, None)

                    let memory = Map.tryFind record.Id memoriesById

                    let isSeed =
                        group
                        |> List.exists (fun item -> item.Kind.StartsWith("Seed", StringComparison.Ordinal))

                    { Id = ExperimentNode record.Id
                      Kind =
                        if isSeed then
                            EvolutionNodeKind.Seed
                        else
                            EvolutionNodeKind.Candidate
                      Sequence = record.Sequence
                      Parent = parent
                      Commit = candidate
                      Outcome = outcome
                      Metric = metric |> Option.orElse (memory |> Option.bind _.Metric)
                      RetainedScore = None
                      Summary = memory |> Option.map _.Summary
                      EvaluationSummary = evaluationSummary
                      Usage = Map.tryFind record.Id usagesById |> Option.defaultValue None
                      StartedAt = record.CreatedAt
                      UpdatedAt = record.UpdatedAt
                      Label =
                        if isSeed then
                            $"Seed {record.Sequence}"
                        else
                            $"Attempt {record.Sequence - seedCount}" })

            let baselineNode =
                { Id = BaselineNode
                  Kind = EvolutionNodeKind.Baseline
                  Sequence = 0
                  Parent = None
                  Commit = baselineCommit
                  Outcome = EvolutionOutcome.Unknown "Baseline"
                  Metric = baselineEvaluation |> Option.bind fst
                  RetainedScore = baselineEvaluation |> Option.bind fst
                  Summary = None
                  EvaluationSummary = baselineEvaluation |> Option.bind snd
                  Usage = None
                  StartedAt = storedRun.CreatedAt
                  UpdatedAt = storedRun.UpdatedAt
                  Label = "Baseline" }

            let scorePoints = Evolution.scorePoints direction baselineNode.Metric nodes

            let retainedById =
                scorePoints
                |> List.map (fun point -> point.NodeId, point.RetainedScore)
                |> Map.ofList

            let nodes =
                nodes
                |> List.map (fun node ->
                    { node with
                        RetainedScore = Map.tryFind node.Id retainedById |> Option.defaultValue None })

            let finalScore = scorePoints |> List.tryLast |> Option.bind _.RetainedScore
            let acceptedCount = Evolution.acceptedCount nodes

            let attemptCount =
                nodes
                |> List.filter (fun node -> node.Kind = EvolutionNodeKind.Candidate)
                |> List.length

            let live = tryCurrent () |> Option.exists (fun state -> state.Id = storedRun.Id)

            let runSummary =
                { Id = storedRun.Id
                  SourcePath = storedRun.SourcePath
                  Status =
                    if storedRun.Status = "Running" && not live then
                        "Interrupted"
                    else
                        storedRun.Status
                  CreatedAt = storedRun.CreatedAt
                  UpdatedAt = storedRun.UpdatedAt
                  MetricName = metricName
                  Direction = direction
                  BaselineCommit = baselineCommit
                  BaselineScore = baselineNode.Metric
                  FrontierScore = finalScore
                  AttemptCount = attemptCount
                  AcceptedCount = acceptedCount }

            let warnings =
                experimentWarnings
                @ evaluationWarnings
                @ memoryWarnings
                @ usageWarnings
                @ eventWarnings
                @ lineageWarnings
                @ (if baselineCommit.IsNone then
                       [ "Baseline commit is unavailable." ]
                   else
                       [])
                @ (if baselineEvaluation.IsNone then
                       [ "Baseline metric is unavailable." ]
                   else
                       [])
                @ (if nodes |> List.exists (fun node -> node.Commit.IsNone) then
                       [ "One or more candidate commits are unavailable." ]
                   else
                       [])

            return
                { Run = runSummary
                  Nodes = nodes
                  Frontier = lineage |> Option.bind _.Frontier |> Option.orElse baselineCommit
                  Warnings = warnings }
        }

    let rec runLoop (cancellationToken: CancellationToken) =
        async {
            match tryCurrent () with
            | Some current when current.Status = Ready ->
                let experimentId = ExperimentId.create ()
                let startedAt = DateTimeOffset.UtcNow
                let transition = dispatch (StartRequested(experimentId, startedAt))

                match transition with
                | None -> return ()
                | Some(startedState, _) ->
                    do!
                        persistExperimentStart
                            startedState.Id
                            experimentId
                            (startedState.Config.SeedPatches.Length + startedState.Attempted)
                            (Some startedState.Frontier)
                            "Active"
                            cancellationToken

                    do!
                        journalEvent
                            startedState.Id
                            (Some experimentId)
                            "CandidatePlanned"
                            (CommitOid.value startedState.Frontier)
                            cancellationToken

                    publish $"Preparing experiment {startedState.Attempted} from retained frontier." (Some experimentId)

                    match!
                        git.PrepareCandidate startedState.Id experimentId startedState.Frontier cancellationToken
                    with
                    | Error error ->
                        dispatch (ExperimentFailed error) |> ignore

                        do!
                            journalEvent
                                startedState.Id
                                (Some experimentId)
                                "PrepareFailed"
                                error.Summary
                                cancellationToken
                    | Ok workspace ->
                        dispatch WorktreePrepared |> ignore

                        let! memoryResult =
                            memory.Select
                                startedState.Id
                                startedState.Config.PromptProfile.MaxMemoryCount
                                startedState.Config.PromptProfile.MaxMemoryCharacters
                                cancellationToken

                        let memories =
                            match memoryResult with
                            | Ok values -> values
                            | Error error ->
                                publish $"Memory warning: {error.Summary}" (Some experimentId)
                                []

                        let prompt =
                            Prompt.build
                                { Objective = startedState.Config.Objective
                                  EditablePaths = startedState.Config.EditablePaths
                                  FrontierScore = startedState.FrontierScore
                                  Metric = startedState.Config.Metric
                                  Profile = startedState.Config.PromptProfile
                                  PreviousEvaluation = startedState.PreviousEvaluation
                                  Memories = memories }

                        let artifactRoot =
                            Path.Combine(DataPaths.artifacts root startedState.Id, ExperimentId.text experimentId)

                        Directory.CreateDirectory artifactRoot |> ignore
                        let schemaPath = Path.Combine(artifactRoot, "experiment-result.schema.json")
                        let promptPath = Path.Combine(artifactRoot, "prompt.md")
                        AtomicFile.writeAllText schemaPath experimentSchema
                        AtomicFile.writeAllText promptPath prompt

                        do!
                            recordArtifact
                                startedState.Id
                                (Some experimentId)
                                "output-schema"
                                schemaPath
                                CancellationToken.None

                        do!
                            recordArtifact
                                startedState.Id
                                (Some experimentId)
                                "prompt"
                                promptPath
                                CancellationToken.None

                        publish "Starting fresh Codex JSONL worker." (Some experimentId)

                        let! codexResult =
                            codex.Run
                                { Executable = codexExecutable
                                  WorkingDirectory = workspace.GenerationPath
                                  Model = startedState.Config.Model
                                  Prompt = prompt
                                  OutputSchemaPath = schemaPath
                                  Timeout = startedState.Config.Budgets.CodexTimeout
                                  JsonlPath = Path.Combine(artifactRoot, "codex.jsonl")
                                  StderrPath = Path.Combine(artifactRoot, "codex.stderr.log") }
                                cancellationToken

                        do!
                            recordArtifact
                                startedState.Id
                                (Some experimentId)
                                "codex-jsonl"
                                (Path.Combine(artifactRoot, "codex.jsonl"))
                                CancellationToken.None

                        do!
                            recordArtifact
                                startedState.Id
                                (Some experimentId)
                                "codex-stderr"
                                (Path.Combine(artifactRoot, "codex.stderr.log"))
                                CancellationToken.None

                        match codexResult with
                        | Error error ->
                            do! captureAfterFailure startedState workspace CancellationToken.None

                            match tryCurrent () with
                            | Some stopping when stopping.Status = Stopping ->
                                dispatch CancellationCompleted |> ignore

                                do!
                                    journalEvent
                                        stopping.Id
                                        (Some experimentId)
                                        "Cancelled"
                                        error.Summary
                                        CancellationToken.None
                            | _ ->
                                dispatch (
                                    ExperimentFailed
                                        { error with
                                            ExperimentId = Some experimentId }
                                )
                                |> ignore

                                do!
                                    journalEvent
                                        startedState.Id
                                        (Some experimentId)
                                        "CodexFailed"
                                        error.Summary
                                        CancellationToken.None
                        | Ok result ->
                            do!
                                journal.SaveUsage startedState.Id experimentId result.Usage CancellationToken.None
                                |> Async.Ignore

                            dispatch (GenerationCompleted(result.ThreadId, result.Usage, result.Summary))
                            |> ignore

                            do!
                                journalEvent
                                    startedState.Id
                                    (Some experimentId)
                                    "GenerationCompleted"
                                    result.ThreadId
                                    CancellationToken.None

                            if result.Usage.IsNone then
                                publish
                                    "Terminal token usage is missing; this candidate will finish, then the run will pause."
                                    (Some experimentId)

                            publish "Codex completed; freezing candidate changes." (Some experimentId)

                            match!
                                git.CaptureCandidate
                                    startedState.Id
                                    workspace
                                    startedState.Config.EditablePaths
                                    CancellationToken.None
                            with
                            | Error error ->
                                dispatch (ExperimentFailed error) |> ignore

                                do!
                                    journalEvent
                                        startedState.Id
                                        (Some experimentId)
                                        "CaptureFailed"
                                        error.Summary
                                        CancellationToken.None
                            | Ok snapshot when not (List.isEmpty snapshot.ProtectedPaths) ->
                                do! persistCandidate startedState.Id experimentId snapshot.Commit CancellationToken.None

                                dispatch (ProtectedPathDetected snapshot.ProtectedPaths) |> ignore
                                let detail = String.concat ", " snapshot.ProtectedPaths

                                do!
                                    journalEvent
                                        startedState.Id
                                        (Some experimentId)
                                        "ProtectedPathRejected"
                                        detail
                                        CancellationToken.None

                                do!
                                    saveMemory
                                        startedState.Id
                                        experimentId
                                        "ProtectedPathRejected"
                                        None
                                        result.Summary
                                        CancellationToken.None
                                    |> Async.Ignore

                                publish $"Rejected protected paths: {detail}" (Some experimentId)
                            | Ok snapshot ->
                                do! persistCandidate startedState.Id experimentId snapshot.Commit CancellationToken.None

                                dispatch (CandidateCaptured snapshot.Commit) |> ignore
                                publish "Running deterministic evaluator in a clean worktree." (Some experimentId)
                                let evaluatorPath = Path.Combine(artifactRoot, "evaluation.json")

                                match!
                                    runEvaluatorWithRetries
                                        startedState.Config.Evaluator
                                        snapshot.FrontierEvaluationPath
                                        snapshot.EvaluationPath
                                        evaluatorPath
                                        CancellationToken.None
                                with
                                | Error error ->
                                    dispatch (ExperimentFailed error) |> ignore

                                    do!
                                        journalEvent
                                            startedState.Id
                                            (Some experimentId)
                                            "EvaluationFailed"
                                            error.Summary
                                            CancellationToken.None

                                    do!
                                        saveMemory
                                            startedState.Id
                                            experimentId
                                            "EvaluationFailed"
                                            None
                                            result.Summary
                                            CancellationToken.None
                                        |> Async.Ignore
                                | Ok evaluation when evaluation.Status = EvaluationStatus.Inconclusive ->
                                    let reason =
                                        if String.IsNullOrWhiteSpace evaluation.Summary then
                                            "Evaluator remained inconclusive after bounded retries."
                                        else
                                            evaluation.Summary

                                    dispatch (EvaluationInconclusive reason) |> ignore

                                    do!
                                        journalEvent
                                            startedState.Id
                                            (Some experimentId)
                                            "EvaluationInconclusive"
                                            reason
                                            CancellationToken.None

                                    publish reason (Some experimentId)
                                | Ok evaluation ->
                                    do!
                                        recordArtifact
                                            startedState.Id
                                            (Some experimentId)
                                            "evaluation"
                                            evaluatorPath
                                            CancellationToken.None

                                    match!
                                        SqliteStore.saveEvaluation
                                            sqlite
                                            startedState.Id
                                            experimentId
                                            evaluation
                                            CancellationToken.None
                                    with
                                    | Ok() -> ()
                                    | Error error ->
                                        publish $"Evaluation journal warning: {error.Summary}" (Some experimentId)

                                    let candidateMetric =
                                        evaluation.Metrics |> Map.tryFind startedState.Config.Metric.Name

                                    let decision =
                                        Evaluation.decide
                                            startedState.Config.Metric
                                            startedState.Config.Evaluator.RequiredConstraints
                                            startedState.FrontierScore
                                            evaluation

                                    let decisionTransition = dispatch (EvaluationCompleted evaluation)

                                    match decision, decisionTransition with
                                    | StrictImprovement score, Some(_, effects) ->
                                        match
                                            effects
                                            |> List.tryPick (function
                                                | PersistAccepted(_, candidate, parent) -> Some(candidate, parent)
                                                | _ -> None)
                                        with
                                        | Some(candidate, parent) ->
                                            match!
                                                journal.AppendEvent
                                                    startedState.Id
                                                    (Some experimentId)
                                                    "AcceptPending"
                                                    (CommitOid.value candidate)
                                                    CancellationToken.None
                                            with
                                            | Error error ->
                                                dispatch (ExperimentFailed error) |> ignore

                                                publish
                                                    $"Acceptance blocked because AcceptPending could not be journaled: {error.Summary}"
                                                    (Some experimentId)
                                            | Ok() ->
                                                match!
                                                    git.AdvanceFrontier
                                                        startedState.Id
                                                        parent
                                                        candidate
                                                        CancellationToken.None
                                                with
                                                | Ok() ->
                                                    dispatch FrontierAdvanced |> ignore

                                                    do!
                                                        journalEvent
                                                            startedState.Id
                                                            (Some experimentId)
                                                            "Accepted"
                                                            (string score)
                                                            CancellationToken.None

                                                    do!
                                                        saveMemory
                                                            startedState.Id
                                                            experimentId
                                                            "Accepted"
                                                            (Some score)
                                                            result.Summary
                                                            CancellationToken.None
                                                        |> Async.Ignore

                                                    publish
                                                        $"Accepted strict improvement: {startedState.FrontierScore} → {score}."
                                                        (Some experimentId)
                                                | Error error ->
                                                    dispatch (ExperimentFailed error) |> ignore

                                                    do!
                                                        journalEvent
                                                            startedState.Id
                                                            (Some experimentId)
                                                            "AcceptFailed"
                                                            error.Summary
                                                            CancellationToken.None
                                        | None ->
                                            let candidateText =
                                                tryCurrent ()
                                                |> Option.bind _.Current
                                                |> Option.bind _.Candidate
                                                |> Option.map CommitOid.value
                                                |> Option.defaultValue "candidate pending"

                                            do!
                                                journalEvent
                                                    startedState.Id
                                                    (Some experimentId)
                                                    "AcceptPending"
                                                    candidateText
                                                    CancellationToken.None

                                            publish "Strict winner is awaiting human review." (Some experimentId)
                                    | CandidateDecision.Rejected reason, _ ->
                                        do!
                                            journalEvent
                                                startedState.Id
                                                (Some experimentId)
                                                "Rejected"
                                                (string reason)
                                                CancellationToken.None

                                        do!
                                            saveMemory
                                                startedState.Id
                                                experimentId
                                                "Rejected"
                                                candidateMetric
                                                result.Summary
                                                CancellationToken.None
                                            |> Async.Ignore

                                        publish
                                            "Candidate did not strictly improve the retained metric."
                                            (Some experimentId)
                                    | _ -> ()

                match tryCurrent () with
                | Some next when next.Status = Ready -> return! runLoop cancellationToken
                | _ -> return ()
            | _ -> return ()
        }

    let rec startWorkerIfReady () =
        let started =
            lock stateGate (fun () ->
                match runCancellation, worker, state with
                | Some cancellation, None, Some current when current.Status = Ready ->
                    let running = runLoop cancellation.Token |> Async.StartAsTask
                    worker <- Some(running :> Task)
                    Some running
                | _ -> None)

        started
        |> Option.iter (fun running ->
            running.ContinueWith(
                Action<Task<unit>>(fun _ ->
                    lock stateGate (fun () -> worker <- None)
                    startWorkerIfReady ())
            )
            |> ignore)

    new(codexExecutable: string) = new HarnessRuntime(DataPaths.root (), codexExecutable)

    member _.State = tryCurrent ()
    member _.StateChanged = stateChanged.Publish
    member _.Activity = activity.Publish
    member _.EvolutionChanged = evolutionChanged.Publish
    member _.DataRoot = root

    member _.ListEvolutionRuns(cancellationToken: CancellationToken) =
        async {
            match SqliteStore.loadRuns sqlite with
            | Error error -> return Error error
            | Ok runs ->
                let summaries =
                    runs
                    |> List.map (fun stored ->
                        let metricName, direction, _ = parseRunConfig stored.ConfigJson

                        { Id = stored.Id
                          SourcePath = stored.SourcePath
                          Status =
                            if
                                stored.Status = "Running"
                                && not (tryCurrent () |> Option.exists (fun value -> value.Id = stored.Id))
                            then
                                "Interrupted"
                            else
                                stored.Status
                          CreatedAt = stored.CreatedAt
                          UpdatedAt = stored.UpdatedAt
                          MetricName = metricName
                          Direction = direction
                          BaselineCommit =
                            stored.ConfigJson
                            |> Option.bind (fun json ->
                                try
                                    use document = JsonDocument.Parse json

                                    Some(
                                        CommitOid.create (document.RootElement.GetProperty("baseCommit").GetString())
                                    )
                                with _ ->
                                    None)
                          BaselineScore = None
                          FrontierScore = None
                          AttemptCount = 0
                          AcceptedCount = 0 })

                return Ok summaries
        }

    member _.LoadEvolution(runId: RunId, cancellationToken: CancellationToken) =
        async {
            match SqliteStore.loadRuns sqlite with
            | Error error -> return Error error
            | Ok runs ->
                match runs |> List.tryFind (fun run -> run.Id = runId) with
                | None ->
                    return
                        Error(
                            HarnessError.create
                                "runtime.evolution_run_missing"
                                HarnessErrorCategory.Persistence
                                "The selected run could not be found."
                        )
                | Some run ->
                    let! snapshot = loadEvolutionSnapshot run cancellationToken
                    return Ok snapshot
        }

    member _.InspectSource(sourcePath: string, cancellationToken: CancellationToken) =
        git.InspectSource sourcePath cancellationToken

    member _.CheckCodex(cancellationToken: CancellationToken) =
        codex.Preflight codexExecutable cancellationToken

    member _.Prepare(config: HarnessConfig, cancellationToken: CancellationToken) =
        async {
            match HarnessConfig.validate config with
            | Error errors ->
                return
                    Error(
                        HarnessError.create
                            "config.invalid"
                            HarnessErrorCategory.Configuration
                            "Harness configuration is invalid."
                        |> HarnessError.withDetail (String.concat " " errors)
                    )
            | Ok validated ->
                match prepared, state with
                | Some _, _
                | _, Some _ ->
                    return
                        Error(
                            HarnessError.create
                                "runtime.already_prepared"
                                HarnessErrorCategory.Configuration
                                "A run is already prepared or active."
                        )
                | None, None ->
                    match! journal.Initialize cancellationToken with
                    | Error error -> return Error error
                    | Ok() ->
                        let! sourceResult = git.InspectSource validated.SourcePath cancellationToken
                        let! codexResult = codex.Preflight codexExecutable cancellationToken

                        match sourceResult, codexResult with
                        | Error error, _
                        | _, Error error -> return Error error
                        | Ok repository, Ok codexReport when repository.Head <> validated.BaseCommit ->
                            return
                                Error(
                                    HarnessError.create
                                        "git.baseline_moved"
                                        HarnessErrorCategory.Git
                                        "Source HEAD no longer matches the configured baseline."
                                )
                        | Ok repository, Ok codexReport ->
                            match validateModel validated codexReport with
                            | Error error -> return Error error
                            | Ok() ->
                                let runId = RunId.create ()
                                let runRoot = DataPaths.runRoot root runId
                                Directory.CreateDirectory runRoot |> ignore

                                match ProjectLock.tryAcquire (DataPaths.projectLock root repository.TopLevel) with
                                | Error detail ->
                                    return
                                        Error(
                                            HarnessError.create
                                                "runtime.locked"
                                                HarnessErrorCategory.Recovery
                                                "Run is already locked."
                                            |> HarnessError.withDetail detail
                                        )
                                | Ok runLock ->
                                    activeLock <- Some runLock

                                    match! git.CreateRun runId repository cancellationToken with
                                    | Error error ->
                                        releaseRunLock ()
                                        return Error error
                                    | Ok() ->
                                        let! savedRun =
                                            SqliteStore.saveRun
                                                sqlite
                                                runId
                                                validated
                                                "PreparingBaseline"
                                                cancellationToken

                                        match savedRun with
                                        | Error error -> publish $"Run journal warning: {error.Summary}" None
                                        | Ok() -> ()

                                        let baselineExperiment = ExperimentId.create ()

                                        do!
                                            persistExperimentStart
                                                runId
                                                baselineExperiment
                                                0
                                                None
                                                "Baseline"
                                                cancellationToken

                                        match!
                                            SqliteStore.updateExperimentCandidate
                                                sqlite
                                                baselineExperiment
                                                validated.BaseCommit
                                                cancellationToken
                                        with
                                        | Error error -> publish $"Lineage journal warning: {error.Summary}" None
                                        | Ok() -> notifyEvolution runId

                                        match!
                                            git.PrepareCandidate
                                                runId
                                                baselineExperiment
                                                validated.BaseCommit
                                                cancellationToken
                                        with
                                        | Error error ->
                                            releaseRunLock ()
                                            return Error error
                                        | Ok baselineWorkspace ->
                                            let baselineResultPath =
                                                Path.Combine(
                                                    DataPaths.artifacts root runId,
                                                    "baseline",
                                                    "evaluation.json"
                                                )

                                            match!
                                                runEvaluatorWithRetries
                                                    validated.Evaluator
                                                    baselineWorkspace.GenerationPath
                                                    baselineWorkspace.GenerationPath
                                                    baselineResultPath
                                                    cancellationToken
                                            with
                                            | Error error ->
                                                releaseRunLock ()
                                                return Error error
                                            | Ok baseline when baseline.Status = EvaluationStatus.Inconclusive ->
                                                releaseRunLock ()

                                                return
                                                    Error(
                                                        HarnessError.create
                                                            "evaluator.baseline_inconclusive"
                                                            HarnessErrorCategory.Evaluation
                                                            "Baseline evaluation remained inconclusive after bounded retries."
                                                    )
                                            | Ok baseline ->
                                                do!
                                                    recordArtifact
                                                        runId
                                                        None
                                                        "baseline-evaluation"
                                                        baselineResultPath
                                                        cancellationToken

                                                match!
                                                    SqliteStore.saveEvaluation
                                                        sqlite
                                                        runId
                                                        baselineExperiment
                                                        baseline
                                                        cancellationToken
                                                with
                                                | Ok() -> ()
                                                | Error error ->
                                                    publish $"Baseline journal warning: {error.Summary}" None

                                                let failed =
                                                    validated.Evaluator.RequiredConstraints
                                                    |> List.filter (fun name ->
                                                        baseline.Constraints |> Map.tryFind name <> Some true)

                                                match failed, baseline.Metrics |> Map.tryFind validated.Metric.Name with
                                                | _ :: _, _ ->
                                                    let failedNames = String.concat ", " failed

                                                    releaseRunLock ()

                                                    return
                                                        Error(
                                                            HarnessError.create
                                                                "evaluator.baseline_constraints"
                                                                HarnessErrorCategory.Evaluation
                                                                $"Baseline failed required constraints: {failedNames}."
                                                        )
                                                | [], None ->
                                                    releaseRunLock ()

                                                    return
                                                        Error(
                                                            HarnessError.create
                                                                "evaluator.baseline_metric_missing"
                                                                HarnessErrorCategory.Evaluation
                                                                $"Baseline evaluator did not return metric '{validated.Metric.Name}'."
                                                        )
                                                | [], Some baselineScore ->
                                                    match!
                                                        evaluateSeedPatches
                                                            runId
                                                            validated
                                                            validated.BaseCommit
                                                            baselineScore
                                                            cancellationToken
                                                    with
                                                    | Error error ->
                                                        releaseRunLock ()
                                                        return Error error
                                                    | Ok(frontier, frontierScore) ->
                                                        match!
                                                            SqliteStore.updateRunStatus
                                                                sqlite
                                                                runId
                                                                "Ready"
                                                                cancellationToken
                                                        with
                                                        | Error error ->
                                                            publish $"Run-status journal warning: {error.Summary}" None
                                                        | Ok() -> ()

                                                        let report =
                                                            { RunId = runId
                                                              Repository = repository
                                                              Codex = codexReport
                                                              Baseline = baseline
                                                              BaselineScore = frontierScore
                                                              DataDirectory = runRoot }

                                                        prepared <- Some report

                                                        setState (
                                                            RunState.create
                                                                runId
                                                                validated
                                                                frontier
                                                                frontierScore
                                                                DateTimeOffset.UtcNow
                                                        )

                                                        do!
                                                            journalEvent
                                                                runId
                                                                None
                                                                "RunPrepared"
                                                                (string frontierScore)
                                                                cancellationToken

                                                        publish "Preflight, baseline, and protected seeds passed." None
                                                        return Ok report
        }

    member _.Start() =
        match prepared, tryCurrent () with
        | Some _, Some current when current.Status = Ready ->
            match
                SqliteStore.updateRunStatus sqlite current.Id "Running" CancellationToken.None
                |> Async.RunSynchronously
            with
            | Error error -> Error error
            | Ok() ->
                runCancellation <- Some(new CancellationTokenSource())
                publish "Run started." None
                startWorkerIfReady ()
                Ok current.Id
        | _ ->
            Error(
                HarnessError.create
                    "runtime.not_prepared"
                    HarnessErrorCategory.Configuration
                    "Prepare and validate the run before starting."
            )

    member _.PauseAfterCurrent() = dispatch PauseRequested |> ignore

    member _.Resume() =
        dispatch ResumeRequested |> ignore
        startWorkerIfReady ()

    member _.StopNow() =
        dispatch StopRequested |> ignore
        runCancellation |> Option.iter _.Cancel()

    member _.ReviewWinner(accept: bool) =
        async {
            match tryCurrent () with
            | Some current ->
                match current.Current with
                | Some active when active.Phase = AwaitingReview ->
                    match active.Candidate, active.Evaluation, active.Summary with
                    | Some candidate, Some evaluation, Some summary ->
                        let metric = evaluation.Metrics[current.Config.Metric.Name]

                        if accept then
                            dispatch ReviewAccepted |> ignore

                            match!
                                journal.AppendEvent
                                    current.Id
                                    (Some active.Id)
                                    "AcceptPending"
                                    (CommitOid.value candidate)
                                    CancellationToken.None
                            with
                            | Error error ->
                                dispatch (ExperimentFailed error) |> ignore

                                publish
                                    $"Acceptance blocked because AcceptPending could not be journaled: {error.Summary}"
                                    (Some active.Id)

                                return Error error
                            | Ok() ->
                                match!
                                    git.AdvanceFrontier current.Id active.Parent candidate CancellationToken.None
                                with
                                | Ok() ->
                                    dispatch FrontierAdvanced |> ignore

                                    do!
                                        journalEvent
                                            current.Id
                                            (Some active.Id)
                                            "AcceptedAfterReview"
                                            (string metric)
                                            CancellationToken.None

                                    do!
                                        saveMemory
                                            current.Id
                                            active.Id
                                            "Accepted"
                                            (Some metric)
                                            summary
                                            CancellationToken.None
                                        |> Async.Ignore

                                    publish
                                        $"Accepted reviewed strict improvement: {current.FrontierScore} → {metric}."
                                        (Some active.Id)

                                    startWorkerIfReady ()
                                    return Ok()
                                | Error error ->
                                    dispatch (ExperimentFailed error) |> ignore

                                    do!
                                        journalEvent
                                            current.Id
                                            (Some active.Id)
                                            "AcceptFailed"
                                            error.Summary
                                            CancellationToken.None

                                    return Error error
                        else
                            dispatch ReviewRejected |> ignore

                            do!
                                journalEvent
                                    current.Id
                                    (Some active.Id)
                                    "RejectedByUser"
                                    (string metric)
                                    CancellationToken.None

                            do!
                                saveMemory
                                    current.Id
                                    active.Id
                                    "RejectedByUser"
                                    (Some metric)
                                    summary
                                    CancellationToken.None
                                |> Async.Ignore

                            publish "Rejected qualified candidate after review; frontier unchanged." (Some active.Id)
                            startWorkerIfReady ()
                            return Ok()
                    | _ ->
                        return
                            Error(
                                HarnessError.create
                                    "runtime.review_incomplete"
                                    HarnessErrorCategory.Recovery
                                    "The candidate awaiting review is incomplete."
                            )
                | _ ->
                    return
                        Error(
                            HarnessError.create
                                "runtime.review_unavailable"
                                HarnessErrorCategory.Configuration
                                "No automatically-qualified candidate is awaiting review."
                        )
            | None ->
                return
                    Error(
                        HarnessError.create
                            "runtime.not_prepared"
                            HarnessErrorCategory.Configuration
                            "No run is active."
                    )
        }

    member _.History(limit: int) = SqliteStore.loadEvents sqlite limit

    member _.CampaignSummary() =
        match prepared, tryCurrent () with
        | Some report, Some current ->
            match SqliteStore.countDuplicateHypotheses sqlite current.Id with
            | Error error -> Error error
            | Ok duplicates ->
                let percentImprovement =
                    match report.BaselineScore with
                    | 0M -> 0M
                    | value ->
                        match current.Config.Metric.Direction with
                        | Maximize -> (current.FrontierScore - value) / abs value * 100M
                        | Minimize -> (value - current.FrontierScore) / abs value * 100M

                let raw = TokenUsage.rawTotal current.Usage

                let tokensPerPercent =
                    if current.AcceptedCount > 0 && percentImprovement > 0M then
                        Some(decimal raw / percentImprovement)
                    else
                        None

                Ok
                    { SchemaVersion = 1
                      RunId = RunId.text current.Id
                      Status = string current.Status
                      InputTokens = current.Usage.InputTokens
                      CachedInputTokens = current.Usage.CachedInputTokens
                      UncachedInputTokens = max 0L (current.Usage.InputTokens - current.Usage.CachedInputTokens)
                      OutputTokens = current.Usage.OutputTokens
                      ReasoningTokens = current.Usage.ReasoningOutputTokens
                      RawTokens = raw
                      Attempts = current.Attempted
                      AcceptedCandidates = current.AcceptedCount
                      EvaluatorRetries = evaluatorRetryCount
                      DuplicateHypotheses = duplicates
                      TokensToFirstQualifiedImprovement =
                        current.UsageAtFirstAcceptance |> Option.map TokenUsage.rawTotal
                      AcceptedPercentImprovement = percentImprovement
                      TokensPerAcceptedOnePercentSpeedup = tokensPerPercent }
        | _ ->
            Error(
                HarnessError.create
                    "runtime.summary_unavailable"
                    HarnessErrorCategory.Configuration
                    "No prepared campaign is available to summarize."
            )

    interface IDisposable with
        member _.Dispose() =
            runCancellation |> Option.iter _.Cancel()
            runCancellation |> Option.iter _.Dispose()
            releaseRunLock ()
