namespace FsHarness.Infrastructure

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsHarness.Codex
open FsHarness.Core

exception RuntimePersistenceAbort of HarnessError

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

type RuntimeHealth =
    { RunId: RunId
      Healthy: bool
      ArtifactCount: int
      PendingOperationCount: int
      WorkPlanCount: int
      KnowledgeClaimCount: int
      Issues: string list }

type private ScheduledGraphExperiment =
    { Kind: ExperimentKind
      Parents: ExperimentParent list
      Champion: CommitOid
      ActiveHeads: Set<CommitOid>
      SynthesisPair: SynthesisPair option
      PreparedSynthesis: (CandidateWorkspace * SynthesisConflict option * bool) option }

type private PendingEvaluationLease =
    { Candidate: CommitOid
      Parents: ExperimentParent list
      Champion: CommitOid
      ChampionScore: decimal
      ParentPath: string
      ChampionPath: string
      CandidatePath: string
      ResultPath: string
      Summary: ExperimentSummary }

type HarnessRuntime(dataRoot: string, codexExecutable: string) =
    let mutable root = Path.GetFullPath dataRoot
    let mutable gitStore = GitStore.create root
    let mutable git = GitStore.port gitStore
    let mutable sqlite = SqliteStore.create (Path.Combine(root, "fsharness.db"))
    let mutable journal = SqliteStore.journalPort sqlite
    let mutable memory = SqliteStore.memoryPort sqlite
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

    let durableState next =
        match persistStatus next with
        | Ok() -> next, None
        | Error error ->
            { next with
                Status = RecoveryRequired error },
            Some error

    let notifyEvolution runId = evolutionChanged.Trigger runId

    let expansionParents commit : ExperimentParent list =
        [ { Commit = commit
            Role = ExperimentParentRole.Primary } ]

    let enterRecovery (error: HarnessError) (experimentId: ExperimentId option) =
        let correlated =
            { error with
                ExperimentId = experimentId }

        let updated =
            lock stateGate (fun () ->
                match state with
                | None -> None
                | Some current ->
                    let next =
                        { current with
                            Status = RecoveryRequired correlated }

                    state <- Some next
                    Some next)

        runCancellation
        |> Option.iter (fun cancellation ->
            try
                cancellation.Cancel()
            with :? ObjectDisposedException ->
                ())

        publish $"Persistence recovery required: {error.Summary}" experimentId
        updated |> Option.iter stateChanged.Trigger

    let persistExperimentStart runId experimentId sequence kind parents championAtStart outcome cancellationToken =
        async {
            let! result =
                match parents with
                | [] -> SqliteStore.beginExperiment sqlite runId experimentId sequence None outcome cancellationToken
                | values ->
                    SqliteStore.beginGraphExperiment
                        sqlite
                        runId
                        experimentId
                        sequence
                        kind
                        values
                        championAtStart
                        outcome
                        cancellationToken

            match result with
            | Ok() -> notifyEvolution runId
            | Error error -> enterRecovery error (Some experimentId)
        }

    let persistCandidate runId experimentId candidate cancellationToken =
        async {
            match! SqliteStore.updateExperimentCandidate sqlite experimentId candidate cancellationToken with
            | Ok() -> notifyEvolution runId
            | Error error -> enterRecovery error (Some experimentId)
        }

    let persistGraphState
        runId
        experimentId
        kind
        championAtStart
        hypothesisFamily
        validity
        decision
        searchStatus
        cancellationToken
        =
        async {
            let synthesisDepth = if kind = ExperimentKind.Synthesis then 1 else 0

            match!
                SqliteStore.saveExperimentGraphState
                    sqlite
                    runId
                    experimentId
                    kind
                    validity
                    decision
                    searchStatus
                    championAtStart
                    hypothesisFamily
                    synthesisDepth
                    cancellationToken
            with
            | Ok() -> notifyEvolution runId
            | Error error -> enterRecovery error (Some experimentId)
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
        | "DuplicateCandidateSkipped" -> Some $"Duplicate: {payload}"
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

    let abortPersistence error =
        releaseRunLock ()
        raise (RuntimePersistenceAbort error)

    let setState next =
        let durable, persistenceError = durableState next
        lock stateGate (fun () -> state <- Some durable)

        persistenceError
        |> Option.iter (fun error -> publish $"Run status persistence failed: {error.Summary}" None)

        stateChanged.Trigger durable

    let dispatch event =
        let result =
            lock stateGate (fun () ->
                match state with
                | None -> None
                | Some current ->
                    let next, effects = RunState.transition DateTimeOffset.UtcNow event current
                    let durable, persistenceError = durableState next
                    state <- Some durable

                    Some(durable, (if persistenceError.IsSome then [] else effects), persistenceError))

        result
        |> Option.iter (fun (next, _, persistenceError) ->
            persistenceError
            |> Option.iter (fun error -> publish $"Run status persistence failed: {error.Summary}" None)

            stateChanged.Trigger next)

        result |> Option.map (fun (next, effects, _) -> next, effects)

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
            | Error error -> enterRecovery error experimentId
        }

    let recordArtifact runId experimentId kind path cancellationToken =
        async {
            match! SqliteStore.saveArtifact sqlite runId experimentId kind path cancellationToken with
            | Ok() -> return ()
            | Error error -> enterRecovery error experimentId
        }

    let recordExperimentKnowledge
        runId
        experimentId
        (parents: ExperimentParent list)
        candidate
        evaluationPath
        (evaluation: EvaluationResult)
        (summary: ExperimentSummary)
        (config: HarnessConfig)
        cancellationToken
        =
        async {
            let primary =
                parents
                |> List.find (fun parent -> parent.Role = ExperimentParentRole.Primary)
                |> _.Commit

            let abortRepositoryUpdate error =
                enterRecovery error (Some experimentId)
                raise (RuntimePersistenceAbort error)

            let entity =
                { Id = KnowledgeEntityId.create ()
                  Kind = KnowledgeEntityKind.Experiment
                  CanonicalName = $"Experiment {ExperimentId.text experimentId}"
                  Attributes =
                    Map
                        [ "experimentId", ExperimentId.text experimentId
                          "parent", CommitOid.value primary
                          "candidate", CommitOid.value candidate ] }

            let source =
                { Id = KnowledgeSourceId.create ()
                  RunId = runId
                  ExperimentId = Some experimentId
                  Kind = KnowledgeSourceKind.Evaluation
                  Location = Path.GetFullPath evaluationPath
                  Sha256 =
                    if File.Exists evaluationPath then
                        Some(AtomicFile.sha256 evaluationPath)
                    else
                        None
                  CapturedAt = DateTimeOffset.UtcNow }

            match! SqliteStore.saveKnowledgeEntity sqlite runId entity cancellationToken with
            | Error error -> publish $"Knowledge graph warning: {error.Summary}" (Some experimentId)
            | Ok() ->
                match! SqliteStore.saveKnowledgeSource sqlite source cancellationToken with
                | Error error -> publish $"Knowledge graph warning: {error.Summary}" (Some experimentId)
                | Ok() ->
                    let claims =
                        { Id = KnowledgeClaimId.create ()
                          RunId = runId
                          Subject = entity.Id
                          Predicate = "tested-hypothesis"
                          Object = KnowledgeValue.Text summary.Hypothesis
                          Confidence = 0.8M
                          Sources = Set.singleton source.Id
                          Supersedes = None
                          CreatedAt = DateTimeOffset.UtcNow }
                        :: (evaluation.Metrics
                            |> Map.toList
                            |> List.map (fun (name, value) ->
                                { Id = KnowledgeClaimId.create ()
                                  RunId = runId
                                  Subject = entity.Id
                                  Predicate = $"metric:{name}"
                                  Object = KnowledgeValue.Number value
                                  Confidence = 1M
                                  Sources = Set.singleton source.Id
                                  Supersedes = None
                                  CreatedAt = DateTimeOffset.UtcNow }))

                    for claim in claims do
                        match! SqliteStore.saveKnowledgeClaim sqlite claim cancellationToken with
                        | Ok() -> ()
                        | Error error -> publish $"Knowledge graph warning: {error.Summary}" (Some experimentId)

                    match SqliteStore.projectIdForRun sqlite runId with
                    | Error error -> abortRepositoryUpdate error
                    | Ok projectId ->
                        match SqliteStore.loadRepositoryKnowledgeGraph sqlite projectId with
                        | Error error -> abortRepositoryUpdate error
                        | Ok repositoryGraph ->
                            let now = DateTimeOffset.UtcNow
                            let runText = RunId.text runId
                            let experimentText = ExperimentId.text experimentId
                            let nodeId prefix value = GraphNodeId.create $"{prefix}:{value}"

                            let edgeId relation fromValue toValue =
                                GraphEdgeId.create $"{relation}:{fromValue}:{toValue}"

                            let runNode = nodeId "agent-run" runText
                            let taskNode = nodeId "task" runText
                            let sourceNode = nodeId "source" experimentText
                            let artifactNode = nodeId "artifact" $"{experimentText}:evaluation"
                            let evaluationNode = nodeId "evaluation" experimentText
                            let claimNode = nodeId "claim" $"{experimentText}:hypothesis"
                            let candidateNode = nodeId "commit" (CommitOid.value candidate)

                            let node kind id name attributes =
                                RepositoryKnowledgeGraph.versionNode runId now kind id name attributes repositoryGraph

                            let sourced relation fromNode toNode =
                                { Id = edgeId (string relation) (GraphNodeId.value fromNode) (GraphNodeId.value toNode)
                                  From = fromNode
                                  Relation = relation
                                  To = toNode
                                  Confidence = 1M
                                  Provenance = GraphProvenance.Sourced(Set.singleton source.Id)
                                  OriginRunId = runId
                                  ValidFrom = now
                                  ValidTo = None }

                            let parentNodes =
                                parents
                                |> List.map (fun parent ->
                                    let id = nodeId "commit" (CommitOid.value parent.Commit)

                                    node GraphNodeKind.Commit id (CommitOid.value parent.Commit) Map.empty)

                            let metricNodes =
                                evaluation.Metrics
                                |> Map.toList
                                |> List.map (fun (name, value) ->
                                    let id = nodeId "metric" $"{experimentText}:{name}"

                                    node GraphNodeKind.Metric id name (Map [ "name", name; "value", string value ]))

                            let constraintNames = String.concat "," config.Evaluator.RequiredConstraints

                            let rubric =
                                $"constraints={constraintNames}; metric={config.Metric.Name}; direction={config.Metric.Direction}"

                            let update =
                                { ProjectId = projectId
                                  RunId = runId
                                  AgentId = "fsharness-headless"
                                  IdempotencyKey = $"experiment-evaluated:{experimentText}"
                                  Nodes =
                                    [ node GraphNodeKind.AgentRun runNode $"Run {runText}" Map.empty
                                      node
                                          GraphNodeKind.Task
                                          taskNode
                                          config.Objective
                                          (Map [ "objective", config.Objective ])
                                      node
                                          GraphNodeKind.Source
                                          sourceNode
                                          evaluationPath
                                          (Map [ "location", evaluationPath ])
                                      node
                                          GraphNodeKind.Artifact
                                          artifactNode
                                          $"Evaluation artifact {experimentText}"
                                          (Map
                                              [ "authoringRun", runText
                                                "artifactVersion", "1"
                                                "path", evaluationPath ])
                                      node
                                          GraphNodeKind.Evaluation
                                          evaluationNode
                                          $"Evaluation {experimentText}"
                                          (Map [ "rubric", rubric; "status", string evaluation.Status ])
                                      node
                                          GraphNodeKind.Claim
                                          claimNode
                                          summary.Hypothesis
                                          (Map [ "hypothesisFamily", summary.HypothesisFamily ])
                                      node GraphNodeKind.Commit candidateNode (CommitOid.value candidate) Map.empty
                                      yield! parentNodes
                                      yield! metricNodes ]
                                  Edges =
                                    [ yield sourced GraphRelationKind.Produced runNode candidateNode
                                      yield sourced GraphRelationKind.DependsOn candidateNode taskNode
                                      yield sourced GraphRelationKind.Evaluates evaluationNode candidateNode
                                      yield sourced GraphRelationKind.Supports artifactNode evaluationNode
                                      yield sourced GraphRelationKind.Supports claimNode candidateNode
                                      for parent in parents do
                                          let parentNode = nodeId "commit" (CommitOid.value parent.Commit)
                                          yield sourced GraphRelationKind.ParentOf parentNode candidateNode
                                      for metricNode in metricNodes do
                                          yield sourced GraphRelationKind.Produced evaluationNode metricNode.Id ] }

                            match RepositoryKnowledgeGraph.apply update repositoryGraph with
                            | Error errors ->
                                HarnessError.create
                                    "runtime.repository_graph_invalid"
                                    HarnessErrorCategory.Persistence
                                    (String.concat " " errors)
                                |> abortRepositoryUpdate
                            | Ok _ ->
                                match! SqliteStore.saveRepositoryGraphUpdate sqlite update cancellationToken with
                                | Ok _ -> ()
                                | Error error -> abortRepositoryUpdate error
        }

    let experimentSchema =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "hypothesis": { "type": "string", "minLength": 1 },
            "hypothesisFamily": { "type": "string", "minLength": 1 },
            "changeSummary": { "type": "string", "minLength": 1 },
            "expectedEffect": { "type": "string", "minLength": 1 },
            "validationNotes": { "type": "array", "items": { "type": "string" } },
            "reusableLesson": { "type": "string", "minLength": 1 }
          },
          "required": ["hypothesisFamily", "hypothesis", "changeSummary", "expectedEffect", "validationNotes", "reusableLesson"]
        }
        """

    let repositoryGraphContext runId objective parent champion maxCharacters =
        SqliteStore.projectIdForRun sqlite runId
        |> Result.bind (fun projectId ->
            SqliteStore.loadRepositoryKnowledgeGraph sqlite projectId
            |> Result.map (fun graph ->
                let commitSeed commit =
                    GraphNodeId.create $"commit:{CommitOid.value commit}"

                let seeds =
                    Set
                        [ GraphNodeId.create $"task:{RunId.text runId}"
                          commitSeed parent
                          commitSeed champion ]

                let context =
                    RepositoryKnowledgeGraph.query
                        { Seeds = seeds
                          MaxHops = 2
                          MaxEdges = 80
                          MaxCharacters = maxCharacters
                          AllowedRelations =
                            Set
                                [ GraphRelationKind.Supports
                                  GraphRelationKind.Contradicts
                                  GraphRelationKind.DerivedFrom
                                  GraphRelationKind.Produced
                                  GraphRelationKind.Evaluates
                                  GraphRelationKind.Supersedes
                                  GraphRelationKind.DependsOn
                                  GraphRelationKind.ParentOf
                                  GraphRelationKind.ResolvedTo ]
                          AsOf = None
                          IncludeConflicts = true }
                        graph

                [ if not (String.IsNullOrWhiteSpace context.Serialized) then
                      yield context.Serialized
                  if context.Truncated then
                      yield "[graph-context truncated]"
                  if not (List.isEmpty context.MissingEvidence) then
                      let ids =
                          context.MissingEvidence |> List.map GraphEdgeId.value |> String.concat ", "

                      yield $"[uncertain/inferred edges: {ids}]"
                  if graph.Nodes.IsEmpty then
                      yield $"[no prior repository graph evidence for objective: {objective}]" ]
                |> function
                    | [] -> None
                    | lines -> Some(String.concat Environment.NewLine lines)))

    let reproducibilityManifest
        runId
        (config: HarnessConfig)
        (repository: RepositoryInspection)
        (preflight: CodexPreflight)
        =
        let evaluatorPath =
            if Path.IsPathRooted config.Evaluator.Executable then
                config.Evaluator.Executable
            elif config.Evaluator.Executable.Contains(Path.DirectorySeparatorChar) then
                Path.GetFullPath(Path.Combine(repository.TopLevel, config.Evaluator.Executable))
            else
                config.Evaluator.Executable

        let evaluatorSha256 =
            if File.Exists evaluatorPath then
                Some(AtomicFile.sha256 evaluatorPath)
            else
                None

        JsonSerializer.Serialize
            {| schemaVersion = 1
               runId = RunId.text runId
               sourcePath = repository.TopLevel
               sourceCommit = CommitOid.value config.BaseCommit
               sourceWasDirty = repository.IsDirty
               objective = config.Objective
               model = config.Model.Id
               reasoningEffort = ReasoningEffort.toConfigValue config.Model.Effort
               codexVersion = preflight.Version
               evaluatorExecutable = config.Evaluator.Executable
               evaluatorSha256 = evaluatorSha256
               operatingSystem = RuntimeInformation.OSDescription
               processArchitecture = string RuntimeInformation.ProcessArchitecture
               framework = RuntimeInformation.FrameworkDescription
               createdAt = DateTimeOffset.UtcNow |}

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

    let isRunLive (storedRun: StoredRun) =
        if tryCurrent () |> Option.exists (fun current -> current.Id = storedRun.Id) then
            true
        elif String.IsNullOrWhiteSpace storedRun.SourcePath then
            false
        else
            match ProjectLock.tryAcquire (DataPaths.projectLock root storedRun.SourcePath) with
            | Error _ -> true
            | Ok probe ->
                probe.Dispose()
                false

    let beginPromotion experimentId =
        match dispatch (PromotionStarted experimentId) with
        | Some(next, _) ->
            next.Current
            |> Option.exists (fun active -> active.Id = experimentId && active.Phase = Promoting)
        | None -> false

    let handleExperimentError runId experimentId kind (error: HarnessError) =
        async {
            let isStopping =
                tryCurrent ()
                |> Option.exists (fun current ->
                    current.Id = runId
                    && current.Status = Stopping
                    && (current.Current |> Option.exists (fun active -> active.Id = experimentId)))

            if isStopping then
                dispatch (CancellationCompleted experimentId) |> ignore

                do! journalEvent runId (Some experimentId) "Cancelled" error.Summary CancellationToken.None
            else
                let correlatedError =
                    { error with
                        ExperimentId = Some experimentId }

                dispatch (ExperimentFailed(experimentId, correlatedError)) |> ignore

                do! journalEvent runId (Some experimentId) kind error.Summary CancellationToken.None
        }

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

    let promoteCandidate
        runId
        experimentId
        parent
        candidate
        score
        summary
        acceptedEvent
        experimentKind
        championAtStart
        =
        async {
            let operationKind = "advance-frontier"

            let operationPayload =
                JsonSerializer.Serialize
                    {| parent = CommitOid.value parent
                       candidate = CommitOid.value candidate
                       score = score |}

            match!
                SqliteStore.beginDurableOperation
                    sqlite
                    runId
                    experimentId
                    operationKind
                    operationPayload
                    CancellationToken.None
            with
            | Error error ->
                do! handleExperimentError runId experimentId "AcceptFailed" error
                return Error error
            | Ok() ->
                match!
                    journal.AppendEvent
                        runId
                        (Some experimentId)
                        "AcceptPending"
                        (CommitOid.value candidate)
                        CancellationToken.None
                with
                | Error error ->
                    do!
                        SqliteStore.completeDurableOperation
                            sqlite
                            runId
                            experimentId
                            operationKind
                            "failed"
                            CancellationToken.None
                        |> Async.Ignore

                    do! handleExperimentError runId experimentId "AcceptFailed" error
                    return Error error
                | Ok() when beginPromotion experimentId ->
                    match! git.AdvanceFrontier runId parent candidate CancellationToken.None with
                    | Error error ->
                        do!
                            SqliteStore.completeDurableOperation
                                sqlite
                                runId
                                experimentId
                                operationKind
                                "failed"
                                CancellationToken.None
                            |> Async.Ignore

                        do! handleExperimentError runId experimentId "AcceptFailed" error
                        return Error error
                    | Ok() ->
                        match!
                            journal.AppendEvent
                                runId
                                (Some experimentId)
                                acceptedEvent
                                (string score)
                                CancellationToken.None
                        with
                        | Error error ->
                            match tryCurrent () with
                            | Some current when current.Id = runId ->
                                setState
                                    { current with
                                        Status = RecoveryRequired error }
                            | _ -> ()

                            return Error error
                        | Ok() ->
                            match!
                                SqliteStore.completeExperiment sqlite experimentId "Accepted" CancellationToken.None
                            with
                            | Error error ->
                                match tryCurrent () with
                                | Some current when current.Id = runId ->
                                    setState
                                        { current with
                                            Status = RecoveryRequired error }
                                | _ -> ()

                                return Error error
                            | Ok() ->
                                notifyEvolution runId

                                do!
                                    saveMemory runId experimentId "Accepted" (Some score) summary CancellationToken.None
                                    |> Async.Ignore

                                let championSequence =
                                    tryCurrent ()
                                    |> Option.map (fun current -> current.AcceptedCount + 1)
                                    |> Option.defaultValue 1

                                do!
                                    SqliteStore.saveChampion
                                        sqlite
                                        runId
                                        championSequence
                                        candidate
                                        (Some score)
                                        (Some experimentId)
                                        CancellationToken.None
                                    |> Async.Ignore

                                let synthesisDepth = if experimentKind = ExperimentKind.Synthesis then 1 else 0

                                match!
                                    SqliteStore.saveExperimentGraphState
                                        sqlite
                                        runId
                                        experimentId
                                        experimentKind
                                        EvaluationValidity.Valid
                                        ChampionDecision.Promoted
                                        SearchStatus.ActiveHead
                                        championAtStart
                                        summary.HypothesisFamily
                                        synthesisDepth
                                        CancellationToken.None
                                with
                                | Error error ->
                                    enterRecovery error (Some experimentId)
                                    return Error error
                                | Ok() ->
                                    do!
                                        SqliteStore.completeDurableOperation
                                            sqlite
                                            runId
                                            experimentId
                                            operationKind
                                            "completed"
                                            CancellationToken.None
                                        |> Async.Ignore

                                    dispatch (ChampionAdvanced experimentId) |> ignore
                                    return Ok()
                | Ok() ->
                    let error =
                        HarnessError.create
                            "runtime.promotion_not_authorized"
                            HarnessErrorCategory.Recovery
                            "Candidate promotion was cancelled before the frontier update began."

                    do!
                        SqliteStore.completeDurableOperation
                            sqlite
                            runId
                            experimentId
                            operationKind
                            "cancelled"
                            CancellationToken.None
                        |> Async.Ignore

                    do! handleExperimentError runId experimentId "AcceptFailed" error
                    return Error error
        }

    let runEvaluatorWithRetries
        runId
        experimentId
        (spec: EvaluatorSpec)
        parentPath
        championPath
        candidatePath
        resultPath
        (cancellationToken: CancellationToken)
        =
        let cancelledError () =
            HarnessError.create
                "evaluator.retry_cancelled"
                HarnessErrorCategory.Evaluation
                "Evaluator retry wait was cancelled."

        let waitForInfrastructure () =
            async {
                try
                    do! Task.Delay(spec.InfrastructureRetryDelay, cancellationToken) |> Async.AwaitTask
                    return true
                with :? OperationCanceledException ->
                    return false
            }

        let rec loop inconclusiveRetries infrastructureRetries =
            async {
                let! result =
                    evaluator.Run
                        { Spec = spec
                          DerivationParentPath = parentPath
                          ChampionPath = championPath
                          CandidatePath = candidatePath
                          ResultPath = resultPath }
                        cancellationToken

                match result with
                | Ok evaluation when
                    evaluation.Status = EvaluationStatus.Inconclusive
                    && inconclusiveRetries < spec.MaxInconclusiveRetries
                    ->
                    evaluatorRetryCount <- evaluatorRetryCount + 1

                    publish
                        $"Evaluator was inconclusive; retrying the same candidate ({inconclusiveRetries + 1}/{spec.MaxInconclusiveRetries})."
                        (Some experimentId)

                    return! loop (inconclusiveRetries + 1) infrastructureRetries
                | Error error when error.Retryable && infrastructureRetries < spec.MaxInfrastructureRetries ->
                    let retryNumber = infrastructureRetries + 1
                    evaluatorRetryCount <- evaluatorRetryCount + 1

                    let detail =
                        $"{error.Code}: {error.Summary} Retrying preserved candidate in {spec.InfrastructureRetryDelay.TotalSeconds:N0}s ({retryNumber}/{spec.MaxInfrastructureRetries})."

                    do! journalEvent runId (Some experimentId) "EvaluatorRetryScheduled" detail CancellationToken.None

                    publish detail (Some experimentId)
                    let! shouldContinue = waitForInfrastructure ()

                    if shouldContinue then
                        return! loop inconclusiveRetries retryNumber
                    else
                        return Error(cancelledError ())
                | _ -> return result
            }

        loop 0 0

    let evaluationLeasePayload
        (snapshot: CandidateSnapshot)
        (schedule: ScheduledGraphExperiment)
        championScore
        resultPath
        (summary: ExperimentSummary)
        =
        JsonSerializer.Serialize
            {| candidate = CommitOid.value snapshot.Commit
               parents =
                schedule.Parents
                |> List.map (fun parent ->
                    {| commit = CommitOid.value parent.Commit
                       role = string parent.Role |})
               champion = CommitOid.value schedule.Champion
               championScore = championScore
               parentPath = snapshot.ParentEvaluationPath
               championPath = snapshot.ChampionEvaluationPath
               candidatePath = snapshot.EvaluationPath
               resultPath = resultPath
               summary =
                {| hypothesisFamily = summary.HypothesisFamily
                   hypothesis = summary.Hypothesis
                   changeSummary = summary.ChangeSummary
                   expectedEffect = summary.ExpectedEffect
                   validationNotes = summary.ValidationNotes
                   reusableLesson = summary.ReusableLesson |} |}

    let parseEvaluationLease (operation: DurableOperation) =
        try
            use document = JsonDocument.Parse operation.Payload
            let root = document.RootElement
            let summary = root.GetProperty "summary"

            let parents =
                root.GetProperty("parents").EnumerateArray()
                |> Seq.map (fun parent ->
                    { Commit = CommitOid.create (parent.GetProperty("commit").GetString())
                      Role =
                        if parent.GetProperty("role").GetString() = "Contributor" then
                            ExperimentParentRole.Contributor
                        else
                            ExperimentParentRole.Primary })
                |> List.ofSeq

            Some
                { Candidate = CommitOid.create (root.GetProperty("candidate").GetString())
                  Parents = parents
                  Champion = CommitOid.create (root.GetProperty("champion").GetString())
                  ChampionScore = root.GetProperty("championScore").GetDecimal()
                  ParentPath = root.GetProperty("parentPath").GetString()
                  ChampionPath = root.GetProperty("championPath").GetString()
                  CandidatePath = root.GetProperty("candidatePath").GetString()
                  ResultPath = root.GetProperty("resultPath").GetString()
                  Summary =
                    { HypothesisFamily = summary.GetProperty("hypothesisFamily").GetString()
                      Hypothesis = summary.GetProperty("hypothesis").GetString()
                      ChangeSummary = summary.GetProperty("changeSummary").GetString()
                      ExpectedEffect = summary.GetProperty("expectedEffect").GetString()
                      ValidationNotes =
                        summary.GetProperty("validationNotes").EnumerateArray()
                        |> Seq.map _.GetString()
                        |> List.ofSeq
                      ReusableLesson = summary.GetProperty("reusableLesson").GetString() } }
        with _ ->
            None

    let evaluateSeedPatches runId (config: HarnessConfig) initialFrontier initialScore cancellationToken =
        let initialParents = expansionParents initialFrontier

        let rankedHeads heads =
            let compareHeads (leftScore, leftCommit) (rightScore, rightCommit) =
                let scoreOrder =
                    match config.Metric.Direction with
                    | Maximize -> compare rightScore leftScore
                    | Minimize -> compare leftScore rightScore

                if scoreOrder <> 0 then
                    scoreOrder
                else
                    StringComparer.Ordinal.Compare(CommitOid.value leftCommit, CommitOid.value rightCommit)

            heads
            |> List.sortWith compareHeads
            |> List.truncate (max 1 config.GraphSearch.BeamWidth)
            |> List.map snd
            |> Set.ofList

        let rec loop index frontier score validHeads remaining =
            async {
                match remaining with
                | [] -> return Ok(frontier, score, rankedHeads validHeads)
                | patchPath :: rest ->
                    let experimentId = ExperimentId.create ()

                    do!
                        persistExperimentStart
                            runId
                            experimentId
                            (index + 1)
                            ExperimentKind.Expansion
                            initialParents
                            initialFrontier
                            "SeedActive"
                            cancellationToken

                    publish
                        $"Evaluating protected seed candidate '{patchPath}' at zero Codex-token cost."
                        (Some experimentId)

                    match! git.PrepareCandidate runId experimentId initialParents initialFrontier cancellationToken with
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
                                        runId
                                        experimentId
                                        config.Evaluator
                                        snapshot.ParentEvaluationPath
                                        snapshot.ChampionEvaluationPath
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

                                        return! loop (index + 1) frontier score validHeads rest
                                    | EvaluationStatus.Complete ->
                                        let isValidSeed =
                                            Evaluation.decide
                                                config.Metric
                                                config.Evaluator.RequiredConstraints
                                                initialScore
                                                evaluation

                                            |> function
                                                | StrictImprovement _
                                                | CandidateDecision.Rejected(NotStrictlyBetter _) -> true
                                                | CandidateDecision.Rejected _ -> false

                                        let validHeads =
                                            if isValidSeed then
                                                match evaluation.Metrics |> Map.tryFind config.Metric.Name with
                                                | Some candidateScore -> (candidateScore, snapshot.Commit) :: validHeads
                                                | None -> validHeads
                                            else
                                                validHeads

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

                                            return! loop (index + 1) frontier score validHeads rest
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

                                                return! loop (index + 1) snapshot.Commit candidateScore validHeads rest
            }

        loop 0 initialFrontier initialScore [] config.SeedPatches

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

    let parseStoredEvaluation (json: string) =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            let schemaVersion = root.GetProperty("schemaVersion").GetInt32()

            let status =
                match root.TryGetProperty "status" with
                | true, value when value.GetString() = "inconclusive" -> EvaluationStatus.Inconclusive
                | _ -> EvaluationStatus.Complete

            let constraints =
                root.GetProperty("constraints").EnumerateObject()
                |> Seq.choose (fun property ->
                    match property.Value.ValueKind with
                    | JsonValueKind.True -> Some(property.Name, true)
                    | JsonValueKind.False -> Some(property.Name, false)
                    | _ -> None)
                |> Map.ofSeq

            let metrics =
                root.GetProperty("metrics").EnumerateObject()
                |> Seq.choose (fun property ->
                    match property.Value.TryGetDecimal() with
                    | true, value -> Some(property.Name, value)
                    | false, _ -> None)
                |> Map.ofSeq

            let summary =
                match root.TryGetProperty "summary" with
                | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
                | _ -> ""

            let evidence =
                match root.TryGetProperty "evidence" with
                | true, value when value.ValueKind = JsonValueKind.Array ->
                    value.EnumerateArray()
                    |> Seq.choose (fun item ->
                        if item.ValueKind = JsonValueKind.String then
                            item.GetString() |> Option.ofObj
                        else
                            None)
                    |> List.ofSeq
                | _ -> []

            Some
                { SchemaVersion = schemaVersion
                  Status = status
                  Constraints = constraints
                  Metrics = metrics
                  Summary = summary
                  Evidence = evidence }
        with _ ->
            None

    let parseAdvanceFrontierOperation (operation: DurableOperation) =
        try
            use document = JsonDocument.Parse operation.Payload
            let root = document.RootElement

            Some(
                CommitOid.create (root.GetProperty("parent").GetString()),
                CommitOid.create (root.GetProperty("candidate").GetString()),
                root.GetProperty("score").GetDecimal()
            )
        with _ ->
            None

    let resumeEvaluationLease
        runId
        (config: HarnessConfig)
        (operation: DurableOperation)
        (lease: PendingEvaluationLease)
        cancellationToken
        =
        async {
            let cachedEvaluation =
                if File.Exists lease.ResultPath then
                    try
                        File.ReadAllText lease.ResultPath |> parseStoredEvaluation
                    with _ ->
                        None
                else
                    None

            let! evaluationResult =
                match cachedEvaluation with
                | Some evaluation -> async { return Ok evaluation }
                | None ->
                    runEvaluatorWithRetries
                        runId
                        operation.ExperimentId
                        config.Evaluator
                        lease.ParentPath
                        lease.ChampionPath
                        lease.CandidatePath
                        lease.ResultPath
                        cancellationToken

            match evaluationResult with
            | Error error -> return Error error
            | Ok evaluation ->
                let existingEvaluation =
                    SqliteStore.loadEvaluationsForRun sqlite runId
                    |> Result.map (List.exists (fun stored -> stored.ExperimentId = operation.ExperimentId))

                match existingEvaluation with
                | Error error -> raise (RuntimePersistenceAbort error)
                | Ok false ->
                    match!
                        SqliteStore.saveEvaluation sqlite runId operation.ExperimentId evaluation CancellationToken.None
                    with
                    | Error error -> raise (RuntimePersistenceAbort error)
                    | Ok() -> ()
                | Ok true -> ()

                do!
                    recordArtifact
                        runId
                        (Some operation.ExperimentId)
                        "evaluation"
                        lease.ResultPath
                        CancellationToken.None

                do!
                    recordExperimentKnowledge
                        runId
                        operation.ExperimentId
                        lease.Parents
                        lease.Candidate
                        lease.ResultPath
                        evaluation
                        lease.Summary
                        config
                        CancellationToken.None

                let! decisionResult =
                    async {
                        match evaluation.Status with
                        | EvaluationStatus.Inconclusive ->
                            let reason =
                                if String.IsNullOrWhiteSpace evaluation.Summary then
                                    "Recovered evaluator result remained inconclusive."
                                else
                                    evaluation.Summary

                            match!
                                SqliteStore.completeExperiment
                                    sqlite
                                    operation.ExperimentId
                                    $"Inconclusive: {reason}"
                                    CancellationToken.None
                            with
                            | Error error -> return Error error
                            | Ok() ->
                                do!
                                    journalEvent
                                        runId
                                        (Some operation.ExperimentId)
                                        "EvaluationInconclusive"
                                        reason
                                        CancellationToken.None

                                return Ok()
                        | EvaluationStatus.Complete ->
                            match
                                Evaluation.decide
                                    config.Metric
                                    config.Evaluator.RequiredConstraints
                                    lease.ChampionScore
                                    evaluation
                            with
                            | StrictImprovement score ->
                                match! GitStore.loadLineage gitStore runId cancellationToken with
                                | Error error -> return Error error
                                | Ok lineage when lineage.Frontier <> Some lease.Champion ->
                                    return
                                        Error(
                                            HarnessError.create
                                                "recovery.evaluation_champion_changed"
                                                HarnessErrorCategory.Recovery
                                                "The champion changed while an evaluator lease was interrupted; fresh champion evidence is required."
                                        )
                                | Ok _ ->
                                    match!
                                        git.AdvanceFrontier runId lease.Champion lease.Candidate CancellationToken.None
                                    with
                                    | Error error -> return Error error
                                    | Ok() ->
                                        match!
                                            SqliteStore.completeExperiment
                                                sqlite
                                                operation.ExperimentId
                                                "Accepted"
                                                CancellationToken.None
                                        with
                                        | Error error -> return Error error
                                        | Ok() ->
                                            let sequence =
                                                SqliteStore.loadExperiments sqlite runId
                                                |> Result.map List.length
                                                |> Result.defaultValue 0

                                            do!
                                                SqliteStore.saveChampion
                                                    sqlite
                                                    runId
                                                    sequence
                                                    lease.Candidate
                                                    (Some score)
                                                    (Some operation.ExperimentId)
                                                    CancellationToken.None
                                                |> Async.Ignore

                                            let recoveredHeads =
                                                SqliteStore.loadActiveHeads sqlite runId
                                                |> Result.map (fun stored ->
                                                    lease.Candidate :: (stored |> List.map _.Commit)
                                                    |> List.distinct
                                                    |> List.truncate config.GraphSearch.BeamWidth)

                                            match recoveredHeads with
                                            | Error error -> raise (RuntimePersistenceAbort error)
                                            | Ok heads ->
                                                match!
                                                    SqliteStore.replaceActiveHeads
                                                        sqlite
                                                        runId
                                                        sequence
                                                        heads
                                                        CancellationToken.None
                                                with
                                                | Error error -> raise (RuntimePersistenceAbort error)
                                                | Ok() -> ()

                                            do!
                                                journalEvent
                                                    runId
                                                    (Some operation.ExperimentId)
                                                    "AcceptedRecoveredEvaluation"
                                                    (string score)
                                                    CancellationToken.None

                                            return Ok()
                            | Rejected reason ->
                                let outcome, retain =
                                    match reason with
                                    | NotStrictlyBetter _ ->
                                        "Rejected: Recovered valid non-winning candidate; retained for graph search.",
                                        true
                                    | ConstraintFailed names ->
                                        let constraintNames = String.concat "," names
                                        $"Rejected: Constraint failed ({constraintNames}).", false
                                    | MetricMissing name -> $"Rejected: Metric missing ({name}).", false

                                match!
                                    SqliteStore.completeExperiment
                                        sqlite
                                        operation.ExperimentId
                                        outcome
                                        CancellationToken.None
                                with
                                | Error error -> return Error error
                                | Ok() ->
                                    if retain then
                                        let heads =
                                            SqliteStore.loadActiveHeads sqlite runId
                                            |> Result.map (fun stored ->
                                                lease.Candidate :: (stored |> List.map _.Commit)
                                                |> List.distinct
                                                |> List.truncate config.GraphSearch.BeamWidth)

                                        match heads with
                                        | Error error -> raise (RuntimePersistenceAbort error)
                                        | Ok values ->
                                            match!
                                                SqliteStore.replaceActiveHeads
                                                    sqlite
                                                    runId
                                                    0
                                                    values
                                                    CancellationToken.None
                                            with
                                            | Error error -> raise (RuntimePersistenceAbort error)
                                            | Ok() -> ()

                                    do!
                                        journalEvent
                                            runId
                                            (Some operation.ExperimentId)
                                            "RejectedRecoveredEvaluation"
                                            outcome
                                            CancellationToken.None

                                    return Ok()
                    }

                match decisionResult with
                | Error error -> return Error error
                | Ok() ->
                    match!
                        SqliteStore.completeDurableOperation
                            sqlite
                            runId
                            operation.ExperimentId
                            operation.Kind
                            "completed"
                            CancellationToken.None
                    with
                    | Error error -> return Error error
                    | Ok() -> return Ok()
        }

    let reconcilePendingOperations runId config cancellationToken =
        async {
            match SqliteStore.loadPendingOperations sqlite runId with
            | Error error -> return Error error
            | Ok [] -> return Ok()
            | Ok operations ->
                match! GitStore.loadLineage gitStore runId cancellationToken with
                | Error error -> return Error error
                | Ok lineage ->
                    let mutable reconciliationError = None

                    for operation in operations do
                        if reconciliationError.IsNone then
                            match operation.Kind with
                            | "evaluate-candidate" ->
                                match parseEvaluationLease operation with
                                | None ->
                                    reconciliationError <-
                                        Some(
                                            HarnessError.create
                                                "recovery.evaluation_lease_invalid"
                                                HarnessErrorCategory.Recovery
                                                "A pending evaluator lease could not be parsed."
                                        )
                                | Some lease ->
                                    let! resumed =
                                        resumeEvaluationLease runId config operation lease cancellationToken
                                        |> Async.Catch

                                    match resumed with
                                    | Choice1Of2(Ok()) -> ()
                                    | Choice1Of2(Error error) -> reconciliationError <- Some error
                                    | Choice2Of2(RuntimePersistenceAbort error) -> reconciliationError <- Some error
                                    | Choice2Of2 error ->
                                        reconciliationError <-
                                            Some(
                                                HarnessError.create
                                                    "recovery.evaluation_resume_failed"
                                                    HarnessErrorCategory.Recovery
                                                    "The pending evaluator lease could not be resumed."
                                                |> HarnessError.withDetail error.Message
                                            )
                            | "advance-frontier" ->
                                match parseAdvanceFrontierOperation operation with
                                | Some(parent, candidate, score) ->
                                    match lineage.Frontier with
                                    | Some frontier when frontier = candidate ->
                                        do!
                                            journalEvent
                                                runId
                                                (Some operation.ExperimentId)
                                                "Accepted"
                                                (string score)
                                                cancellationToken

                                        do!
                                            SqliteStore.completeDurableOperation
                                                sqlite
                                                runId
                                                operation.ExperimentId
                                                operation.Kind
                                                "completed"
                                                cancellationToken
                                            |> Async.Ignore
                                    | Some frontier when frontier = parent ->
                                        do!
                                            journalEvent
                                                runId
                                                (Some operation.ExperimentId)
                                                "Cancelled"
                                                "Recovered before frontier promotion."
                                                cancellationToken

                                        do!
                                            SqliteStore.completeDurableOperation
                                                sqlite
                                                runId
                                                operation.ExperimentId
                                                operation.Kind
                                                "cancelled"
                                                cancellationToken
                                            |> Async.Ignore
                                    | _ ->
                                        reconciliationError <-
                                            Some(
                                                HarnessError.create
                                                    "recovery.frontier_conflict"
                                                    HarnessErrorCategory.Recovery
                                                    "The private Git frontier does not match either side of a pending promotion."
                                            )
                                | None ->
                                    reconciliationError <-
                                        Some(
                                            HarnessError.create
                                                "recovery.operation_invalid"
                                                HarnessErrorCategory.Recovery
                                                "A pending frontier operation could not be parsed."
                                        )
                            | _ ->
                                reconciliationError <-
                                    Some(
                                        HarnessError.create
                                            "recovery.operation_invalid"
                                            HarnessErrorCategory.Recovery
                                            "A pending durable operation could not be parsed."
                                    )

                    return
                        match reconciliationError with
                        | Some error -> Error error
                        | None -> Ok()
        }

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
            | "DuplicateCandidateSkipped" -> Some(EvolutionOutcome.Rejected $"Duplicate: {event.Payload}")
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

            let storedEdges, edgeWarnings =
                match SqliteStore.loadExperimentEdges sqlite storedRun.Id with
                | Ok value -> value, []
                | Error error -> [], [ error.Summary ]

            let storedHeads, headWarnings =
                match SqliteStore.loadActiveHeads sqlite storedRun.Id with
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
                match SqliteStore.loadEventsForRun sqlite storedRun.Id with
                | Ok value -> value, []
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
                |> List.filter (fun node ->
                    node.Kind = EvolutionNodeKind.Candidate
                    && (match node.Outcome with
                        | EvolutionOutcome.Rejected reason ->
                            not (reason.StartsWith("Duplicate:", StringComparison.Ordinal))
                        | _ -> true))
                |> List.length

            let live = isRunLive storedRun

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
                @ edgeWarnings
                @ headWarnings
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

            let champion = lineage |> Option.bind _.Frontier |> Option.orElse baselineCommit

            let edges =
                storedEdges
                |> List.choose (fun edge ->
                    edge.Child
                    |> Option.map (fun child ->
                        { Id = edge.Id
                          Parent = edge.Parent
                          Child = child
                          Kind = edge.Kind
                          Role = edge.Role }))

            let activeHeads =
                match storedHeads |> List.map _.Commit |> Set.ofList with
                | heads when not (Set.isEmpty heads) -> heads
                | _ -> champion |> Option.map Set.singleton |> Option.defaultValue Set.empty

            return
                { Run = runSummary
                  Nodes = nodes
                  Frontier = champion
                  Edges = edges
                  Champion = champion
                  ActiveHeads = activeHeads
                  Warnings = warnings }
        }

    let searchGraphFromEvolution (graphStates: StoredExperimentGraphState list) (snapshot: EvolutionSnapshot) =
        match snapshot.Run.BaselineCommit, snapshot.Champion with
        | Some baseline, Some champion ->
            let stateByExperiment =
                graphStates |> List.map (fun state -> state.ExperimentId, state) |> Map.ofList

            let baselineNode =
                { Commit = baseline
                  Parents = []
                  Metric = snapshot.Run.BaselineScore
                  HypothesisFamily = "baseline"
                  Validity = EvaluationValidity.Valid
                  ChampionDecision = ChampionDecision.Promoted
                  SearchStatus =
                    if Set.contains baseline snapshot.ActiveHeads then
                        SearchStatus.ActiveHead
                    else
                        SearchStatus.Retained
                  Sequence = 0
                  SynthesisDepth = 0 }

            let edgesByChild = snapshot.Edges |> List.groupBy _.Child |> Map.ofList

            let rec synthesisDepth visited commit =
                if commit = baseline || Set.contains commit visited then
                    0
                else
                    match Map.tryFind commit edgesByChild with
                    | None
                    | Some [] -> 0
                    | Some parentEdges ->
                        let parentDepth =
                            parentEdges
                            |> List.map (fun edge -> synthesisDepth (Set.add commit visited) edge.Parent)
                            |> List.max

                        if parentEdges |> List.exists (fun edge -> edge.Kind = ExperimentKind.Synthesis) then
                            parentDepth + 1
                        else
                            parentDepth

            let nodes =
                snapshot.Nodes
                |> List.sortBy _.Sequence
                |> List.choose (fun node ->
                    node.Commit
                    |> Option.map (fun commit ->
                        let storedState =
                            match node.Id with
                            | ExperimentNode experimentId -> Map.tryFind experimentId stateByExperiment
                            | BaselineNode -> None

                        let parentEdges = snapshot.Edges |> List.filter (fun edge -> edge.Child = commit)

                        let parents =
                            if List.isEmpty parentEdges then
                                node.Parent
                                |> Option.map (fun parent ->
                                    [ { Commit = parent
                                        Role = ExperimentParentRole.Primary } ])
                                |> Option.defaultValue []
                            else
                                parentEdges
                                |> List.map (fun edge ->
                                    { Commit = edge.Parent
                                      Role = edge.Role })

                        let validity, status =
                            match storedState with
                            | Some state -> state.Validity, state.SearchStatus
                            | None ->
                                match node.Outcome with
                                | EvolutionOutcome.Accepted -> EvaluationValidity.Valid, SearchStatus.Retained
                                | EvolutionOutcome.Rejected reason when
                                    node.Metric.IsSome
                                    && not (reason.Contains("Constraint", StringComparison.OrdinalIgnoreCase))
                                    && not (reason.Contains("Protected", StringComparison.OrdinalIgnoreCase))
                                    ->
                                    EvaluationValidity.Valid, SearchStatus.Retained
                                | EvolutionOutcome.Inconclusive reason ->
                                    EvaluationValidity.Inconclusive reason, SearchStatus.Archived
                                | EvolutionOutcome.Failed reason ->
                                    EvaluationValidity.InfrastructureFailed reason, SearchStatus.Archived
                                | EvolutionOutcome.Cancelled ->
                                    EvaluationValidity.InfrastructureFailed "Cancelled", SearchStatus.Archived
                                | EvolutionOutcome.Active _ -> EvaluationValidity.Pending, SearchStatus.Archived
                                | _ -> EvaluationValidity.ConstraintFailed [], SearchStatus.Archived

                        let status =
                            if Set.contains commit snapshot.ActiveHeads && validity = EvaluationValidity.Valid then
                                SearchStatus.ActiveHead
                            else
                                status

                        { Commit = commit
                          Parents = parents
                          Metric = node.Metric
                          HypothesisFamily =
                            storedState
                            |> Option.map _.HypothesisFamily
                            |> Option.orElseWith (fun () -> node.Summary |> Option.map _.HypothesisFamily)
                            |> Option.defaultValue "legacy"
                          Validity = validity
                          ChampionDecision =
                            storedState
                            |> Option.map _.ChampionDecision
                            |> Option.defaultWith (fun () ->
                                if commit = champion then
                                    ChampionDecision.Promoted
                                else
                                    ChampionDecision.NotPromoted)
                          SearchStatus = status
                          Sequence = node.Sequence
                          SynthesisDepth = synthesisDepth Set.empty commit }))

            let graph =
                { Baseline = baseline
                  Champion = champion
                  Direction = snapshot.Run.Direction
                  Nodes =
                    nodes
                    |> List.map (fun node -> node.Commit, node)
                    |> Map.ofList
                    |> Map.add baseline baselineNode }

            GraphSearch.validate graph
        | _ ->
            Error(
                HarnessError.create
                    "runtime.search_graph_root_missing"
                    HarnessErrorCategory.Recovery
                    "Cannot schedule graph work without a baseline and champion."
            )

    let storedRunFor runId =
        SqliteStore.loadRuns sqlite
        |> Result.bind (fun runs ->
            match runs |> List.tryFind (fun run -> run.Id = runId) with
            | Some run -> Ok run
            | None ->
                Error(
                    HarnessError.create
                        "runtime.search_run_missing"
                        HarnessErrorCategory.Recovery
                        "The persisted run for graph scheduling is missing."
                ))

    let rec scheduleGraphExperiment (current: RunState) experimentId cancellationToken =
        async {
            match storedRunFor current.Id with
            | Error error -> return Error error
            | Ok storedRun ->
                let! snapshot = loadEvolutionSnapshot storedRun cancellationToken

                match SqliteStore.loadExperimentGraphStates sqlite current.Id with
                | Error error -> return Error error
                | Ok graphStates ->
                    match searchGraphFromEvolution graphStates snapshot with
                    | Error error -> return Error error
                    | Ok graph ->
                        let ordinaryNodes =
                            snapshot.Nodes
                            |> List.filter (fun node -> node.Sequence > current.Config.SeedPatches.Length)
                            |> List.sortBy _.Sequence

                        if ordinaryNodes.Length < current.Config.GraphSearch.InitialFanOut then
                            let seedHeads =
                                snapshot.ActiveHeads
                                |> Set.toList
                                |> List.filter (fun head ->
                                    match Map.tryFind head graph.Nodes with
                                    | Some node -> node.Validity = EvaluationValidity.Valid
                                    | None -> false)

                            let bootstrapRoot =
                                match seedHeads with
                                | [] -> graph.Champion
                                | heads -> heads[ordinaryNodes.Length % heads.Length]

                            let heads =
                                if List.isEmpty seedHeads then
                                    GraphSearch.selectActiveHeads current.Config.GraphSearch.BeamWidth graph
                                    |> Set.ofList
                                else
                                    Set.ofList seedHeads

                            return
                                Ok
                                    { Kind = ExperimentKind.Expansion
                                      Parents = expansionParents bootstrapRoot
                                      Champion = graph.Champion
                                      ActiveHeads = heads
                                      SynthesisPair = None
                                      PreparedSynthesis = None }
                        else
                            let latestRound = SqliteStore.loadLatestSearchRound sqlite current.Id

                            match latestRound with
                            | Error error -> return Error error
                            | Ok(Some round) when
                                round.Status = "active"
                                && round.Heads
                                   |> List.exists (fun head -> not (Set.contains head round.CompletedHeads))
                                ->
                                let parent =
                                    round.Heads
                                    |> List.find (fun head -> not (Set.contains head round.CompletedHeads))

                                let updated =
                                    { round with
                                        CompletedHeads = Set.add parent round.CompletedHeads
                                        OrdinarySinceSynthesis = round.OrdinarySinceSynthesis + 1
                                        UpdatedAt = DateTimeOffset.UtcNow }

                                match! SqliteStore.saveSearchRound sqlite updated cancellationToken with
                                | Error error -> return Error error
                                | Ok() ->
                                    return
                                        Ok
                                            { Kind = ExperimentKind.Expansion
                                              Parents = expansionParents parent
                                              Champion = graph.Champion
                                              ActiveHeads = Set.ofList round.Heads
                                              SynthesisPair = None
                                              PreparedSynthesis = None }
                            | Ok latest ->
                                let previousNumber = latest |> Option.map _.Round |> Option.defaultValue 0

                                let ordinarySince =
                                    latest
                                    |> Option.map _.OrdinarySinceSynthesis
                                    |> Option.defaultValue ordinaryNodes.Length

                                let heads = GraphSearch.selectActiveHeads current.Config.GraphSearch.BeamWidth graph
                                let attempts = SqliteStore.loadSynthesisAttempts sqlite current.Id

                                match attempts with
                                | Error error -> return Error error
                                | Ok attempts ->
                                    let attemptedPairs =
                                        attempts
                                        |> List.filter (fun attempt -> attempt.PolicyVersion = 1)
                                        |> List.map (fun attempt ->
                                            if
                                                StringComparer.Ordinal.Compare(
                                                    CommitOid.value attempt.Primary,
                                                    CommitOid.value attempt.Contributor
                                                )
                                                <= 0
                                            then
                                                attempt.Primary, attempt.Contributor
                                            else
                                                attempt.Contributor, attempt.Primary)
                                        |> Set.ofList

                                    let synthesisLimit =
                                        GraphSearch.reservedSynthesisSlots
                                            current.Config.GraphSearch
                                            current.Config.Budgets.MaxExperiments

                                    let lastWasSynthesis =
                                        snapshot.Nodes
                                        |> List.sortBy _.Sequence
                                        |> List.tryLast
                                        |> Option.bind _.Commit
                                        |> Option.exists (fun commit ->
                                            snapshot.Edges
                                            |> List.exists (fun edge ->
                                                edge.Child = commit && edge.Kind = ExperimentKind.Synthesis))

                                    let shouldSynthesize =
                                        GraphSearch.shouldSynthesize
                                            current.Config.GraphSearch
                                            ordinarySince
                                            current.ConsecutiveNonImprovements
                                        && (attempts
                                            |> List.filter (fun attempt -> attempt.ExperimentId.IsSome)
                                            |> List.length) < synthesisLimit
                                        && not lastWasSynthesis

                                    let candidatePairs =
                                        if shouldSynthesize then
                                            GraphSearch.synthesisPairs
                                                current.Config.GraphSearch.MaxSynthesisDepth
                                                attemptedPairs
                                                heads
                                                graph
                                        else
                                            []

                                    let rec analyzePairs pending collected =
                                        async {
                                            match pending with
                                            | [] -> return Ok(List.rev collected)
                                            | pair :: rest ->
                                                match!
                                                    git.AnalyzeSynthesis
                                                        current.Id
                                                        pair.Primary
                                                        pair.Contributor
                                                        cancellationToken
                                                with
                                                | Error error -> return Error error
                                                | Ok analysis ->
                                                    return! analyzePairs rest ((pair, analysis) :: collected)
                                        }

                                    let pairRank (pair, analysis) =
                                        let primary = Map.find pair.Primary graph.Nodes
                                        let contributor = Map.find pair.Contributor graph.Nodes

                                        let distinctFamily =
                                            if
                                                String.Equals(
                                                    primary.HypothesisFamily,
                                                    contributor.HypothesisFamily,
                                                    StringComparison.OrdinalIgnoreCase
                                                )
                                            then
                                                1
                                            else
                                                0

                                        let values = [ primary.Metric; contributor.Metric ] |> List.choose id

                                        let worst, best =
                                            match graph.Direction, values with
                                            | _, [] -> None, None
                                            | Maximize, metrics -> Some(List.min metrics), Some(List.max metrics)
                                            | Minimize, metrics -> Some(List.max metrics), Some(List.min metrics)

                                        let metricKey =
                                            match graph.Direction with
                                            | Maximize -> Option.map (~-) >> Option.defaultValue Decimal.MaxValue
                                            | Minimize -> Option.defaultValue Decimal.MaxValue

                                        (if analysis.CleanMerge then 0 else 1),
                                        distinctFamily,
                                        analysis.ChangedPathOverlap,
                                        metricKey worst,
                                        metricKey best,
                                        CommitOid.value pair.Primary,
                                        CommitOid.value pair.Contributor

                                    let! synthesisPairResult =
                                        async {
                                            match! analyzePairs candidatePairs [] with
                                            | Error error -> return Error error
                                            | Ok analyzedPairs ->
                                                return
                                                    analyzedPairs
                                                    |> List.sortBy pairRank
                                                    |> List.tryHead
                                                    |> Option.map fst
                                                    |> Ok
                                        }

                                    match synthesisPairResult with
                                    | Error error -> return Error error
                                    | Ok(Some pair) ->
                                        let attempt =
                                            { RunId = current.Id
                                              Primary = pair.Primary
                                              Contributor = pair.Contributor
                                              PolicyVersion = 1
                                              Status = "scheduled"
                                              ExperimentId = None
                                              FailureDetail = None
                                              UpdatedAt = DateTimeOffset.UtcNow }

                                        match! SqliteStore.saveSynthesisAttempt sqlite attempt cancellationToken with
                                        | Error error -> return Error error
                                        | Ok() ->
                                            let closedRound =
                                                { RunId = current.Id
                                                  Round = previousNumber
                                                  Heads = heads
                                                  CompletedHeads = Set.ofList heads
                                                  OrdinarySinceSynthesis = 0
                                                  Status = "synthesis-scheduled"
                                                  UpdatedAt = DateTimeOffset.UtcNow }

                                            match! SqliteStore.saveSearchRound sqlite closedRound cancellationToken with
                                            | Error error -> return Error error
                                            | Ok() ->
                                                let! prepared =
                                                    git.PrepareSynthesis
                                                        current.Id
                                                        experimentId
                                                        pair.Primary
                                                        pair.Contributor
                                                        graph.Champion
                                                        cancellationToken

                                                let acceptedPreparation, rejection =
                                                    match prepared with
                                                    | Error error -> None, Some error
                                                    | Ok(SynthesisPreparation.Clean workspace) ->
                                                        Some(workspace, None, true), None
                                                    | Ok(SynthesisPreparation.Conflicted(_, _)) when
                                                        current.Config.GraphSearch.ConflictResolutionAttempts <= 0
                                                        ->
                                                        None,
                                                        Some(
                                                            HarnessError.create
                                                                "git.synthesis_conflict_repair_disabled"
                                                                HarnessErrorCategory.Git
                                                                "The synthesis merge conflicted and automatic repair is disabled."
                                                        )
                                                    | Ok(SynthesisPreparation.Conflicted(workspace, conflict)) when
                                                        conflict.Files.Length
                                                        <= current.Config.GraphSearch.MaxConflictFiles
                                                        && conflict.ConflictText.Length
                                                           <= current.Config.GraphSearch.MaxConflictCharacters
                                                        ->
                                                        Some(workspace, Some conflict, false), None
                                                    | Ok(SynthesisPreparation.Conflicted(_, conflict)) ->
                                                        None,
                                                        Some(
                                                            HarnessError.create
                                                                "git.synthesis_conflict_limit"
                                                                HarnessErrorCategory.Git
                                                                "The synthesis conflict exceeded the configured automatic-repair limit."
                                                            |> HarnessError.withDetail
                                                                $"files={conflict.Files.Length}; characters={conflict.ConflictText.Length}"
                                                        )

                                                match acceptedPreparation, rejection, prepared with
                                                | Some preparation, _, _ ->
                                                    return
                                                        Ok
                                                            { Kind = ExperimentKind.Synthesis
                                                              Parents =
                                                                [ { Commit = pair.Primary
                                                                    Role = ExperimentParentRole.Primary }
                                                                  { Commit = pair.Contributor
                                                                    Role = ExperimentParentRole.Contributor } ]
                                                              Champion = graph.Champion
                                                              ActiveHeads = Set.ofList heads
                                                              SynthesisPair = Some pair
                                                              PreparedSynthesis = Some preparation }
                                                | None, Some error, Error _ -> return Error error
                                                | None, Some error, _ ->
                                                    match!
                                                        SqliteStore.saveSynthesisAttempt
                                                            sqlite
                                                            { attempt with
                                                                Status = "rejected-preflight"
                                                                FailureDetail = Some error.Summary
                                                                UpdatedAt = DateTimeOffset.UtcNow }
                                                            cancellationToken
                                                    with
                                                    | Error persistenceError -> return Error persistenceError
                                                    | Ok() ->
                                                        publish
                                                            $"Synthesis preflight rejected without consuming an experiment slot: {error.Summary}"
                                                            None

                                                        return!
                                                            scheduleGraphExperiment
                                                                current
                                                                experimentId
                                                                cancellationToken
                                                | _ ->
                                                    return
                                                        Error(
                                                            HarnessError.create
                                                                "git.synthesis_preflight"
                                                                HarnessErrorCategory.Git
                                                                "Synthesis preflight failed."
                                                        )
                                    | Ok None ->
                                        let nextRound =
                                            { RunId = current.Id
                                              Round = previousNumber + 1
                                              Heads = heads
                                              CompletedHeads = Set.empty
                                              OrdinarySinceSynthesis = ordinarySince
                                              Status = "active"
                                              UpdatedAt = DateTimeOffset.UtcNow }

                                        match!
                                            SqliteStore.replaceActiveHeads
                                                sqlite
                                                current.Id
                                                nextRound.Round
                                                heads
                                                cancellationToken
                                        with
                                        | Error error -> return Error error
                                        | Ok() ->
                                            match! SqliteStore.saveSearchRound sqlite nextRound cancellationToken with
                                            | Error error -> return Error error
                                            | Ok() ->
                                                return! scheduleGraphExperiment current experimentId cancellationToken
        }

    let rec runLoop (cancellationToken: CancellationToken) =
        async {
            match tryCurrent () with
            | Some current when current.Status = Ready ->
                let experimentId = ExperimentId.create ()
                let startedAt = DateTimeOffset.UtcNow
                let! scheduleResult = scheduleGraphExperiment current experimentId cancellationToken

                match scheduleResult with
                | Error error ->
                    enterRecovery error None
                    return ()
                | Ok _ -> ()

                let schedule = scheduleResult |> Result.toOption |> Option.get

                setState
                    { current with
                        ActiveHeads = schedule.ActiveHeads }

                let transition =
                    dispatch (
                        GraphExperimentRequested(
                            experimentId,
                            schedule.Kind,
                            schedule.Parents,
                            schedule.Champion,
                            startedAt
                        )
                    )

                match transition with
                | None -> return ()
                | Some(startedState, _) when
                    startedState.Current |> Option.exists (fun active -> active.Id = experimentId)
                    ->
                    do!
                        persistExperimentStart
                            startedState.Id
                            experimentId
                            (startedState.Config.SeedPatches.Length + startedState.Attempted)
                            schedule.Kind
                            schedule.Parents
                            schedule.Champion
                            "Active"
                            cancellationToken

                    let workPlan =
                        WorkPlan.standardExperiment
                            startedState.Id
                            experimentId
                            startedState.Config.Objective
                            startedState.Config.Budgets
                            startedState.Config.Evaluator.Timeout
                            startedAt

                    match WorkPlan.validate workPlan with
                    | Error errors ->
                        let detail = String.concat " " errors

                        enterRecovery
                            (HarnessError.create
                                "runtime.plan_invalid"
                                HarnessErrorCategory.Recovery
                                "The generated typed work plan is invalid."
                             |> HarnessError.withDetail detail)
                            (Some experimentId)
                    | Ok _ ->
                        match! SqliteStore.saveWorkPlan sqlite workPlan cancellationToken with
                        | Error error -> enterRecovery error (Some experimentId)
                        | Ok() ->
                            do!
                                journalEvent
                                    startedState.Id
                                    (Some experimentId)
                                    "PlanCreated"
                                    (WorkPlanId.text workPlan.Id)
                                    cancellationToken

                    do!
                        journalEvent
                            startedState.Id
                            (Some experimentId)
                            "CandidatePlanned"
                            (schedule.Parents
                             |> List.map (fun parent -> CommitOid.value parent.Commit)
                             |> String.concat ",")
                            cancellationToken

                    publish
                        $"Preparing {schedule.Kind} experiment {startedState.Attempted} against champion {CommitOid.value schedule.Champion}."
                        (Some experimentId)

                    let! preparationResult =
                        async {
                            match schedule.PreparedSynthesis with
                            | Some preparation -> return Ok preparation
                            | _ ->
                                let! prepared =
                                    git.PrepareCandidate
                                        startedState.Id
                                        experimentId
                                        schedule.Parents
                                        schedule.Champion
                                        cancellationToken

                                return prepared |> Result.map (fun workspace -> workspace, None, false)
                        }

                    match preparationResult with
                    | Error error -> do! handleExperimentError startedState.Id experimentId "PrepareFailed" error
                    | Ok(workspace, synthesisConflict, cleanSynthesis) ->
                        dispatch (WorktreePrepared experimentId) |> ignore

                        let primary =
                            schedule.Parents
                            |> List.find (fun parent -> parent.Role = ExperimentParentRole.Primary)
                            |> _.Commit

                        let repositoryContext =
                            match
                                repositoryGraphContext
                                    startedState.Id
                                    startedState.Config.Objective
                                    primary
                                    schedule.Champion
                                    startedState.Config.PromptProfile.MaxMemoryCharacters
                            with
                            | Ok context -> context
                            | Error error ->
                                publish $"Repository graph warning: {error.Summary}" (Some experimentId)
                                None

                        let synthesisGraphContext =
                            synthesisConflict
                            |> Option.map (fun conflict ->
                                let files = String.concat ", " conflict.Files
                                $"Conflicted files: {files}\n\n{conflict.ConflictText}")

                        let graphContext =
                            [ repositoryContext; synthesisGraphContext ]
                            |> List.choose id
                            |> function
                                | [] -> None
                                | sections -> Some(String.concat "\n\n" sections)

                        let prompt =
                            Prompt.build
                                { Objective =
                                    match schedule.Kind with
                                    | ExperimentKind.Expansion -> startedState.Config.Objective
                                    | ExperimentKind.Synthesis ->
                                        $"Resolve the prepared two-parent synthesis while preserving both compatible improvements. {startedState.Config.Objective}"
                                  EditablePaths = startedState.Config.EditablePaths
                                  ParentCommit =
                                    startedState.Current
                                    |> Option.bind (fun active ->
                                        active.Parents
                                        |> List.tryFind (fun parent -> parent.Role = ExperimentParentRole.Primary)
                                        |> Option.map _.Commit)
                                  ChampionCommit = Some startedState.Champion
                                  ChampionScore = startedState.ChampionScore
                                  Metric = startedState.Config.Metric
                                  Profile = startedState.Config.PromptProfile
                                  PreviousEvaluation = startedState.PreviousEvaluation
                                  Memories = []
                                  GraphContext = graphContext }

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

                        if cleanSynthesis then
                            publish
                                "Clean two-parent merge prepared; skipping generation and evaluating directly."
                                (Some experimentId)
                        else
                            publish "Starting fresh Codex JSONL worker." (Some experimentId)

                        let! codexResult =
                            if cleanSynthesis then
                                async {
                                    let parentLabels =
                                        schedule.Parents
                                        |> List.map (fun parent -> CommitOid.value parent.Commit)
                                        |> String.concat " + "

                                    return
                                        Ok
                                            { ThreadId = $"synthesis:{ExperimentId.text experimentId}"
                                              Usage = Some TokenUsage.zero
                                              Summary =
                                                { HypothesisFamily = "automatic-synthesis"
                                                  Hypothesis = $"Combine compatible retained lineages {parentLabels}."
                                                  ChangeSummary =
                                                    "Created a deterministic two-parent Git merge candidate."
                                                  ExpectedEffect =
                                                    "Preserve compatible improvements from both parent lineages."
                                                  ValidationNotes =
                                                    [ "Clean three-way merge; full evaluator required." ]
                                                  ReusableLesson =
                                                    "Automatic synthesis is retained only after champion-relative evaluation." }
                                              ExitCode = 0
                                              SawThreadStarted = true }
                                }
                            else
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

                        let combineUsage first second =
                            match first, second with
                            | Some left, Some right -> Some(TokenUsage.add left right)
                            | _ -> None

                        let! codexResult, duplicateCheck =
                            async {
                                match codexResult, cleanSynthesis with
                                | Ok first, false ->
                                    match!
                                        git.CheckEditableTree
                                            startedState.Id
                                            workspace
                                            startedState.Config.EditablePaths
                                            cancellationToken
                                    with
                                    | Error error -> return Error error, None
                                    | Ok check when check.MatchingExperiment.IsSome ->
                                        let matching = check.MatchingExperiment.Value
                                        let matchingText = ExperimentId.text matching

                                        do!
                                            journalEvent
                                                startedState.Id
                                                (Some experimentId)
                                                "DuplicateTreeDetected"
                                                $"fingerprint={check.Fingerprint};matchingExperiment={matchingText}"
                                                CancellationToken.None

                                        let retryPrompt =
                                            prompt
                                            + "\n\nThe editable tree you produced is an exact duplicate of experiment "
                                            + matchingText
                                            + " (fingerprint "
                                            + check.Fingerprint
                                            + "). Regenerate once: choose a materially different, still single-change hypothesis. Do not repeat this fingerprint.\n"

                                        let retryPromptPath = Path.Combine(artifactRoot, "prompt-regeneration.md")
                                        AtomicFile.writeAllText retryPromptPath retryPrompt

                                        do!
                                            recordArtifact
                                                startedState.Id
                                                (Some experimentId)
                                                "duplicate-regeneration-prompt"
                                                retryPromptPath
                                                CancellationToken.None

                                        publish
                                            $"Duplicate editable tree matched experiment {matchingText}; allowing one regeneration attempt."
                                            (Some experimentId)

                                        let! retry =
                                            codex.Run
                                                { Executable = codexExecutable
                                                  WorkingDirectory = workspace.GenerationPath
                                                  Model = startedState.Config.Model
                                                  Prompt = retryPrompt
                                                  OutputSchemaPath = schemaPath
                                                  Timeout = startedState.Config.Budgets.CodexTimeout
                                                  JsonlPath = Path.Combine(artifactRoot, "codex-regeneration.jsonl")
                                                  StderrPath =
                                                    Path.Combine(artifactRoot, "codex-regeneration.stderr.log") }
                                                cancellationToken

                                        do!
                                            recordArtifact
                                                startedState.Id
                                                (Some experimentId)
                                                "codex-regeneration-jsonl"
                                                (Path.Combine(artifactRoot, "codex-regeneration.jsonl"))
                                                CancellationToken.None

                                        do!
                                            recordArtifact
                                                startedState.Id
                                                (Some experimentId)
                                                "codex-regeneration-stderr"
                                                (Path.Combine(artifactRoot, "codex-regeneration.stderr.log"))
                                                CancellationToken.None

                                        match retry with
                                        | Error error -> return Error error, None
                                        | Ok regenerated ->
                                            let combined =
                                                { regenerated with
                                                    Usage = combineUsage first.Usage regenerated.Usage }

                                            match!
                                                git.CheckEditableTree
                                                    startedState.Id
                                                    workspace
                                                    startedState.Config.EditablePaths
                                                    cancellationToken
                                            with
                                            | Error error -> return Error error, None
                                            | Ok second when second.MatchingExperiment.IsSome ->
                                                return Ok combined, Some second
                                            | Ok _ -> return Ok combined, None
                                    | Ok _ -> return Ok first, None
                                | value, _ -> return value, None
                            }

                        if not cleanSynthesis then
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

                        match duplicateCheck, codexResult with
                        | Some duplicate, Ok result ->
                            let matching = duplicate.MatchingExperiment.Value

                            dispatch (
                                DuplicateCandidateSkipped(experimentId, duplicate.Fingerprint, matching, result.Usage)
                            )
                            |> ignore

                            do!
                                journal.SaveUsage startedState.Id experimentId result.Usage CancellationToken.None
                                |> Async.Ignore

                            do!
                                journalEvent
                                    startedState.Id
                                    (Some experimentId)
                                    "DuplicateCandidateSkipped"
                                    $"fingerprint={duplicate.Fingerprint};matchingExperiment={ExperimentId.text matching}"
                                    CancellationToken.None

                            publish
                                $"Skipped duplicate tree {duplicate.Fingerprint}; it does not consume an experiment or non-improvement slot."
                                (Some experimentId)
                        | None, Error error ->
                            do! captureAfterFailure startedState workspace CancellationToken.None
                            do! handleExperimentError startedState.Id experimentId "CodexFailed" error
                        | None, Ok result ->
                            do!
                                journal.SaveUsage startedState.Id experimentId result.Usage CancellationToken.None
                                |> Async.Ignore

                            dispatch (GenerationCompleted(experimentId, result.ThreadId, result.Usage, result.Summary))
                            |> ignore

                            do!
                                persistGraphState
                                    startedState.Id
                                    experimentId
                                    schedule.Kind
                                    schedule.Champion
                                    result.Summary.HypothesisFamily
                                    EvaluationValidity.Pending
                                    ChampionDecision.Pending
                                    SearchStatus.Archived
                                    CancellationToken.None

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
                                    cancellationToken
                            with
                            | Error error ->
                                do! handleExperimentError startedState.Id experimentId "CaptureFailed" error
                            | Ok snapshot when List.isEmpty snapshot.ChangedPaths ->
                                let error =
                                    HarnessError.create
                                        "git.candidate_empty"
                                        HarnessErrorCategory.Git
                                        "The candidate did not produce any editable changes."

                                do! handleExperimentError startedState.Id experimentId "CaptureFailed" error
                            | Ok snapshot when not (List.isEmpty snapshot.ProtectedPaths) ->
                                do! persistCandidate startedState.Id experimentId snapshot.Commit CancellationToken.None

                                do!
                                    persistGraphState
                                        startedState.Id
                                        experimentId
                                        schedule.Kind
                                        schedule.Champion
                                        result.Summary.HypothesisFamily
                                        (EvaluationValidity.ConstraintFailed snapshot.ProtectedPaths)
                                        ChampionDecision.NotPromoted
                                        SearchStatus.Archived
                                        CancellationToken.None

                                dispatch (ProtectedPathDetected(experimentId, snapshot.ProtectedPaths))
                                |> ignore

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

                                dispatch (CandidateCaptured(experimentId, snapshot.Commit)) |> ignore
                                publish "Running deterministic evaluator in a clean worktree." (Some experimentId)
                                let evaluatorPath = Path.Combine(artifactRoot, "evaluation.json")

                                let evaluationOperation = "evaluate-candidate"

                                let leasePayload =
                                    evaluationLeasePayload
                                        snapshot
                                        schedule
                                        startedState.ChampionScore
                                        evaluatorPath
                                        result.Summary

                                let! leaseResult =
                                    SqliteStore.beginDurableOperation
                                        sqlite
                                        startedState.Id
                                        experimentId
                                        evaluationOperation
                                        leasePayload
                                        CancellationToken.None

                                let! evaluationResult =
                                    async {
                                        match leaseResult with
                                        | Error error -> return Error error
                                        | Ok() ->
                                            let! evaluated =
                                                runEvaluatorWithRetries
                                                    startedState.Id
                                                    experimentId
                                                    startedState.Config.Evaluator
                                                    snapshot.ParentEvaluationPath
                                                    snapshot.ChampionEvaluationPath
                                                    snapshot.EvaluationPath
                                                    evaluatorPath
                                                    cancellationToken

                                            match evaluated with
                                            | Ok evaluation ->
                                                match!
                                                    SqliteStore.saveEvaluation
                                                        sqlite
                                                        startedState.Id
                                                        experimentId
                                                        evaluation
                                                        CancellationToken.None
                                                with
                                                | Error error -> return Error error
                                                | Ok() -> return Ok evaluation
                                            | Error error when error.Retryable -> return Error error
                                            | Error error ->
                                                do!
                                                    SqliteStore.completeDurableOperation
                                                        sqlite
                                                        startedState.Id
                                                        experimentId
                                                        evaluationOperation
                                                        "failed"
                                                        CancellationToken.None
                                                    |> Async.Ignore

                                                return Error error
                                    }

                                match evaluationResult with
                                | Error error when error.Retryable ->
                                    enterRecovery error (Some experimentId)
                                    publish "Evaluator lease remains pending for recovery." (Some experimentId)
                                | Error error ->
                                    do!
                                        persistGraphState
                                            startedState.Id
                                            experimentId
                                            schedule.Kind
                                            schedule.Champion
                                            result.Summary.HypothesisFamily
                                            (EvaluationValidity.InfrastructureFailed error.Summary)
                                            ChampionDecision.NotPromoted
                                            SearchStatus.Archived
                                            CancellationToken.None

                                    do! handleExperimentError startedState.Id experimentId "EvaluationFailed" error

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

                                    dispatch (EvaluationInconclusive(experimentId, reason)) |> ignore

                                    do!
                                        persistGraphState
                                            startedState.Id
                                            experimentId
                                            schedule.Kind
                                            schedule.Champion
                                            result.Summary.HypothesisFamily
                                            (EvaluationValidity.Inconclusive reason)
                                            ChampionDecision.NotPromoted
                                            SearchStatus.Archived
                                            CancellationToken.None

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

                                    do!
                                        recordExperimentKnowledge
                                            startedState.Id
                                            experimentId
                                            schedule.Parents
                                            snapshot.Commit
                                            evaluatorPath
                                            evaluation
                                            result.Summary
                                            startedState.Config
                                            CancellationToken.None

                                    let candidateMetric =
                                        evaluation.Metrics |> Map.tryFind startedState.Config.Metric.Name

                                    let decision =
                                        Evaluation.decide
                                            startedState.Config.Metric
                                            startedState.Config.Evaluator.RequiredConstraints
                                            startedState.ChampionScore
                                            evaluation

                                    let validity, championDecision, searchStatus =
                                        match decision with
                                        | StrictImprovement _ ->
                                            EvaluationValidity.Valid, ChampionDecision.Pending, SearchStatus.Retained
                                        | CandidateDecision.Rejected(NotStrictlyBetter _) ->
                                            EvaluationValidity.Valid,
                                            ChampionDecision.NotPromoted,
                                            SearchStatus.ActiveHead
                                        | CandidateDecision.Rejected(ConstraintFailed constraints) ->
                                            EvaluationValidity.ConstraintFailed constraints,
                                            ChampionDecision.NotPromoted,
                                            SearchStatus.Archived
                                        | CandidateDecision.Rejected(MetricMissing name) ->
                                            EvaluationValidity.Inconclusive $"Metric missing: {name}",
                                            ChampionDecision.NotPromoted,
                                            SearchStatus.Archived

                                    do!
                                        persistGraphState
                                            startedState.Id
                                            experimentId
                                            schedule.Kind
                                            schedule.Champion
                                            result.Summary.HypothesisFamily
                                            validity
                                            championDecision
                                            searchStatus
                                            CancellationToken.None

                                    let decisionTransition = dispatch (EvaluationCompleted(experimentId, evaluation))

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
                                                promoteCandidate
                                                    startedState.Id
                                                    experimentId
                                                    parent
                                                    candidate
                                                    score
                                                    result.Summary
                                                    "Accepted"
                                                    schedule.Kind
                                                    schedule.Champion
                                            with
                                            | Error error ->
                                                publish $"Acceptance failed safely: {error.Summary}" (Some experimentId)
                                            | Ok() ->
                                                publish
                                                    $"Accepted strict improvement: {startedState.ChampionScore} → {score}."
                                                    (Some experimentId)
                                        | None ->
                                            let awaitingReview =
                                                tryCurrent ()
                                                |> Option.bind _.Current
                                                |> Option.filter (fun active ->
                                                    active.Id = experimentId && active.Phase = AwaitingReview)

                                            match awaitingReview with
                                            | Some active ->
                                                do!
                                                    journalEvent
                                                        startedState.Id
                                                        (Some experimentId)
                                                        "AcceptPending"
                                                        (active.Candidate
                                                         |> Option.map CommitOid.value
                                                         |> Option.defaultValue "candidate pending")
                                                        CancellationToken.None

                                                publish "Strict winner is awaiting human review." (Some experimentId)
                                            | None ->
                                                let error =
                                                    HarnessError.create
                                                        "runtime.decision_not_current"
                                                        HarnessErrorCategory.Recovery
                                                        "The evaluation completed after its experiment was no longer current."

                                                do!
                                                    handleExperimentError
                                                        startedState.Id
                                                        experimentId
                                                        "EvaluationFailed"
                                                        error
                                    | CandidateDecision.Rejected reason, Some(next, _) when next.Current.IsNone ->
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
                                    | CandidateDecision.Rejected _, _ ->
                                        let error =
                                            HarnessError.create
                                                "runtime.rejection_not_current"
                                                HarnessErrorCategory.Recovery
                                                "The evaluation completed after its experiment was no longer current."

                                        do! handleExperimentError startedState.Id experimentId "EvaluationFailed" error
                                    | _ -> ()

                                match evaluationResult with
                                | Ok _ ->
                                    do!
                                        SqliteStore.completeDurableOperation
                                            sqlite
                                            startedState.Id
                                            experimentId
                                            evaluationOperation
                                            "completed"
                                            CancellationToken.None
                                        |> Async.Ignore
                                | _ -> ()

                        | Some _, Error error ->
                            do! handleExperimentError startedState.Id experimentId "CodexFailed" error

                    let planStatus =
                        match tryCurrent () |> Option.bind _.Current with
                        | Some active when active.Id = experimentId && active.Phase = AwaitingReview ->
                            "awaiting-review"
                        | Some active when active.Id = experimentId -> "active"
                        | _ -> "finished"

                    match schedule.SynthesisPair with
                    | Some pair ->
                        do!
                            SqliteStore.saveSynthesisAttempt
                                sqlite
                                { RunId = startedState.Id
                                  Primary = pair.Primary
                                  Contributor = pair.Contributor
                                  PolicyVersion = 1
                                  Status = planStatus
                                  ExperimentId = Some experimentId
                                  FailureDetail = None
                                  UpdatedAt = DateTimeOffset.UtcNow }
                                CancellationToken.None
                            |> Async.Ignore
                    | None -> ()

                    let! workPlanStatusResult =
                        SqliteStore.updateWorkPlanStatus sqlite workPlan.Id planStatus CancellationToken.None

                    match workPlanStatusResult with
                    | Ok() -> ()
                    | Error error -> enterRecovery error (Some experimentId)

                    let persistedCandidate =
                        match SqliteStore.loadExperiments sqlite startedState.Id with
                        | Ok experiments ->
                            experiments
                            |> List.exists (fun experiment ->
                                experiment.Id = experimentId && experiment.Candidate.IsSome)
                        | Error error ->
                            publish $"Terminal worktree cleanup warning: {error.Summary}" (Some experimentId)
                            false

                    if
                        planStatus = "finished"
                        && Result.isOk workPlanStatusResult
                        && persistedCandidate
                    then
                        match! git.ReleaseExperimentWorktrees startedState.Id experimentId CancellationToken.None with
                        | Ok removed when removed > 0 ->
                            do!
                                journalEvent
                                    startedState.Id
                                    (Some experimentId)
                                    "ExperimentWorktreesReleased"
                                    (string removed)
                                    CancellationToken.None

                            publish $"Released {removed} terminal experiment worktree(s)." (Some experimentId)
                        | Ok _ -> ()
                        | Error error ->
                            do!
                                journalEvent
                                    startedState.Id
                                    (Some experimentId)
                                    "ExperimentWorktreeCleanupFailed"
                                    error.Summary
                                    CancellationToken.None

                            publish $"Terminal worktree cleanup warning: {error.Summary}" (Some experimentId)

                | Some _ -> ()

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

    member _.IsWorkerActive =
        lock stateGate (fun () -> worker |> Option.exists (fun task -> not task.IsCompleted))

    member _.StateChanged = stateChanged.Publish
    member _.Activity = activity.Publish
    member _.EvolutionChanged = evolutionChanged.Publish
    member _.DataRoot = root

    member _.TrySetDataRoot(nextRoot: string) =
        try
            if String.IsNullOrWhiteSpace nextRoot then
                Error "The FsHarness data root cannot be empty."
            else
                let normalized = Path.GetFullPath nextRoot

                lock stateGate (fun () ->
                    match runCancellation, worker, activeLock with
                    | Some _, _, _
                    | _, Some _, _
                    | _, _, Some _ ->
                        Error
                            "The data root cannot change while a run is active or prepared. Stop or discard the run first."
                    | None, None, None ->
                        Directory.CreateDirectory normalized |> ignore

                        if not (String.Equals(root, normalized, StringComparison.OrdinalIgnoreCase)) then
                            root <- normalized
                            gitStore <- GitStore.create root
                            git <- GitStore.port gitStore
                            sqlite <- SqliteStore.create (Path.Combine(root, "fsharness.db"))
                            journal <- SqliteStore.journalPort sqlite
                            memory <- SqliteStore.memoryPort sqlite
                            state <- None
                            prepared <- None
                            evaluatorRetryCount <- 0

                        Ok root)
        with error ->
            Error $"Unable to use data root '{nextRoot}': {error.Message}"

    member _.ListEvolutionRuns(cancellationToken: CancellationToken) =
        async {
            match! SqliteStore.initialize sqlite cancellationToken with
            | Error error -> return Error error
            | Ok() ->
                match SqliteStore.loadRuns sqlite with
                | Error error -> return Error error
                | Ok runs ->
                    let rec loadSummaries remaining accumulated =
                        async {
                            match remaining with
                            | [] -> return List.rev accumulated
                            | stored :: tail ->
                                let! snapshot = loadEvolutionSnapshot stored cancellationToken
                                return! loadSummaries tail (snapshot.Run :: accumulated)
                        }

                    let! summaries = loadSummaries runs []

                    return Ok summaries
        }

    member _.LoadEvolution(runId: RunId, cancellationToken: CancellationToken) =
        async {
            match! SqliteStore.initialize sqlite cancellationToken with
            | Error error -> return Error error
            | Ok() ->
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

    member _.LoadRepositoryKnowledge(runId: RunId, cancellationToken: CancellationToken) =
        async {
            match! SqliteStore.initialize sqlite cancellationToken with
            | Error error -> return Error error
            | Ok() ->
                match SqliteStore.projectIdForRun sqlite runId with
                | Error error -> return Error error
                | Ok projectId -> return SqliteStore.loadRepositoryKnowledgeGraph sqlite projectId
        }

    member this.LoadWorkGraph(runId: RunId, cancellationToken: CancellationToken) =
        async {
            match! this.LoadEvolution(runId, cancellationToken) with
            | Error error -> return Error error
            | Ok snapshot -> return WorkGraph.ofEvolution snapshot
        }

    member this.WorkGraphChildren(runId: RunId, parent: CommitOid, cancellationToken: CancellationToken) =
        async {
            match! this.LoadWorkGraph(runId, cancellationToken) with
            | Error error -> return Error error
            | Ok graph when WorkGraph.contains parent graph -> return Ok(WorkGraph.children parent graph)
            | Ok _ ->
                return
                    Error(
                        HarnessError.create
                            "work_graph.parent_unknown"
                            HarnessErrorCategory.Recovery
                            "The requested parent commit is not present in the work graph."
                    )
        }

    member this.WorkGraphParents(runId: RunId, child: CommitOid, cancellationToken: CancellationToken) =
        async {
            match! this.LoadWorkGraph(runId, cancellationToken) with
            | Error error -> return Error error
            | Ok graph when WorkGraph.contains child graph -> return Ok(WorkGraph.parents child graph)
            | Ok _ ->
                return
                    Error(
                        HarnessError.create
                            "work_graph.child_unknown"
                            HarnessErrorCategory.Recovery
                            "The requested child commit is not present in the work graph."
                    )
        }

    member this.WorkGraphAncestors(runId: RunId, commit: CommitOid, cancellationToken: CancellationToken) =
        async {
            match! this.LoadWorkGraph(runId, cancellationToken) with
            | Error error -> return Error error
            | Ok graph when WorkGraph.contains commit graph ->
                return
                    WorkGraph.ancestors commit graph
                    |> Set.toList
                    |> List.choose (fun ancestor -> WorkGraph.tryFind ancestor graph)
                    |> Ok
            | Ok _ ->
                return
                    Error(
                        HarnessError.create
                            "work_graph.commit_unknown"
                            HarnessErrorCategory.Recovery
                            "The requested commit is not present in the work graph."
                    )
        }

    member this.WorkGraphDescendants(runId: RunId, commit: CommitOid, cancellationToken: CancellationToken) =
        async {
            match! this.LoadWorkGraph(runId, cancellationToken) with
            | Error error -> return Error error
            | Ok graph when WorkGraph.contains commit graph ->
                return
                    WorkGraph.descendants commit graph
                    |> Set.toList
                    |> List.choose (fun descendant -> WorkGraph.tryFind descendant graph)
                    |> Ok
            | Ok _ ->
                return
                    Error(
                        HarnessError.create
                            "work_graph.commit_unknown"
                            HarnessErrorCategory.Recovery
                            "The requested commit is not present in the work graph."
                    )
        }

    member this.WorkGraphActiveHeads(runId: RunId, cancellationToken: CancellationToken) =
        async {
            match! this.LoadWorkGraph(runId, cancellationToken) with
            | Error error -> return Error error
            | Ok graph -> return Ok(WorkGraph.activeHeads graph)
        }

    member _.SynthesisAttempts(runId: RunId, cancellationToken: CancellationToken) =
        async {
            match! SqliteStore.initialize sqlite cancellationToken with
            | Error error -> return Error error
            | Ok() -> return SqliteStore.loadSynthesisAttempts sqlite runId
        }

    member this.WorkGraphLeaves(runId: RunId, cancellationToken: CancellationToken) =
        async {
            match! this.LoadWorkGraph(runId, cancellationToken) with
            | Error error -> return Error error
            | Ok graph -> return Ok(WorkGraph.leaves graph)
        }

    member this.WorkGraphLineage(runId: RunId, commit: CommitOid, cancellationToken: CancellationToken) =
        async {
            match! this.LoadWorkGraph(runId, cancellationToken) with
            | Error error -> return Error error
            | Ok graph -> return WorkGraph.lineage commit graph
        }

    member this.WorkGraphDiff
        (runId: RunId, fromCommit: CommitOid, toCommit: CommitOid, cancellationToken: CancellationToken)
        =
        async {
            match! this.LoadWorkGraph(runId, cancellationToken) with
            | Error error -> return Error error
            | Ok graph when
                not (WorkGraph.contains fromCommit graph)
                || not (WorkGraph.contains toCommit graph)
                ->
                return
                    Error(
                        HarnessError.create
                            "work_graph.diff_commit_unknown"
                            HarnessErrorCategory.Recovery
                            "Both diff commits must be present in the selected work graph."
                    )
            | Ok _ -> return! GitStore.diffCommits gitStore runId fromCommit toCommit cancellationToken
        }

    member _.UpsertKnowledgeEntity(runId: RunId, entity: KnowledgeEntity, cancellationToken: CancellationToken) =
        async {
            match! SqliteStore.initialize sqlite cancellationToken with
            | Error error -> return Error error
            | Ok() -> return! SqliteStore.saveKnowledgeEntity sqlite runId entity cancellationToken
        }

    member _.AddKnowledgeAlias
        (runId: RunId, entityId: KnowledgeEntityId, alias: string, cancellationToken: CancellationToken)
        =
        async {
            match! SqliteStore.initialize sqlite cancellationToken with
            | Error error -> return Error error
            | Ok() -> return! SqliteStore.saveKnowledgeAlias sqlite runId entityId alias cancellationToken
        }

    member _.AddKnowledgeSource(source: KnowledgeSource, cancellationToken: CancellationToken) =
        async {
            match! SqliteStore.initialize sqlite cancellationToken with
            | Error error -> return Error error
            | Ok() -> return! SqliteStore.saveKnowledgeSource sqlite source cancellationToken
        }

    member _.AddKnowledgeClaim(claim: KnowledgeClaim, cancellationToken: CancellationToken) =
        async {
            match! SqliteStore.initialize sqlite cancellationToken with
            | Error error -> return Error error
            | Ok() ->
                match SqliteStore.loadKnowledgeGraph sqlite claim.RunId with
                | Error error -> return Error error
                | Ok graph ->
                    match KnowledgeGraph.addClaim claim graph with
                    | Error detail ->
                        return
                            Error(
                                HarnessError.create
                                    "knowledge.claim_invalid"
                                    HarnessErrorCategory.Configuration
                                    "The knowledge claim is invalid."
                                |> HarnessError.withDetail detail
                            )
                    | Ok _ -> return! SqliteStore.saveKnowledgeClaim sqlite claim cancellationToken
        }

    member _.SearchKnowledge(runId: RunId, query: string, limit: int, cancellationToken: CancellationToken) =
        async {
            match! SqliteStore.initialize sqlite cancellationToken with
            | Error error -> return Error error
            | Ok() ->
                return
                    SqliteStore.loadKnowledgeGraph sqlite runId
                    |> Result.map (KnowledgeGraph.search query limit)
        }

    member _.AddAnnotation(annotation: RunAnnotation, cancellationToken: CancellationToken) =
        async {
            match RunAnnotation.validate annotation with
            | Error errors ->
                return
                    Error(
                        HarnessError.create
                            "annotation.invalid"
                            HarnessErrorCategory.Configuration
                            "The run annotation is invalid."
                        |> HarnessError.withDetail (String.concat " " errors)
                    )
            | Ok validated ->
                match! SqliteStore.initialize sqlite cancellationToken with
                | Error error -> return Error error
                | Ok() ->
                    match! SqliteStore.saveAnnotation sqlite validated cancellationToken with
                    | Error error -> return Error error
                    | Ok() ->
                        let experimentId =
                            match validated.Target with
                            | AnnotationTarget.Experiment id -> Some id
                            | _ -> None

                        do!
                            journalEvent
                                validated.RunId
                                experimentId
                                "AnnotationAdded"
                                (RunAnnotationId.text validated.Id)
                                cancellationToken

                        return Ok validated
        }

    member _.LoadAnnotations(runId: RunId, cancellationToken: CancellationToken) =
        async {
            match! SqliteStore.initialize sqlite cancellationToken with
            | Error error -> return Error error
            | Ok() -> return SqliteStore.loadAnnotations sqlite runId
        }

    member _.LoadWorkPlans(runId: RunId, cancellationToken: CancellationToken) =
        async {
            match! SqliteStore.initialize sqlite cancellationToken with
            | Error error -> return Error error
            | Ok() -> return SqliteStore.loadWorkPlans sqlite runId
        }

    member _.ExecuteWorkPlan
        (
            plan: WorkPlan,
            parent: CommitOid,
            maxParallelism: int,
            availableTools: Set<ToolCapability>,
            rawTokensRemaining: int64,
            deadline: DateTimeOffset,
            handler: WorkItemHandler,
            cancellationToken: CancellationToken
        ) =
        PlanExecutor.run maxParallelism parent availableTools rawTokensRemaining deadline handler cancellationToken plan

    member _.HealthCheck(runId: RunId, cancellationToken: CancellationToken) =
        async {
            match! SqliteStore.initialize sqlite cancellationToken with
            | Error error -> return Error error
            | Ok() ->
                match SqliteStore.loadRuns sqlite with
                | Error error -> return Error error
                | Ok runs when runs |> List.exists (fun run -> run.Id = runId) |> not ->
                    return
                        Error(
                            HarnessError.create
                                "health.run_missing"
                                HarnessErrorCategory.Recovery
                                "The requested run does not exist."
                        )
                | Ok _ ->
                    let! lineageResult = GitStore.loadLineage gitStore runId cancellationToken
                    let artifactsResult = SqliteStore.loadArtifactsForRun sqlite runId
                    let operationsResult = SqliteStore.loadPendingOperations sqlite runId
                    let plansResult = SqliteStore.loadWorkPlans sqlite runId
                    let knowledgeResult = SqliteStore.loadKnowledgeGraph sqlite runId

                    let artifactCount, artifactIssues =
                        match artifactsResult with
                        | Error error -> 0, [ error.Summary ]
                        | Ok artifacts ->
                            artifacts.Length,
                            (artifacts
                             |> List.choose (fun artifact ->
                                 match SqliteStore.verifyArtifact artifact with
                                 | Ok() -> None
                                 | Error detail -> Some detail))

                    let pendingCount, operationIssues =
                        match operationsResult with
                        | Error error -> 0, [ error.Summary ]
                        | Ok operations ->
                            operations.Length,
                            (if List.isEmpty operations then
                                 []
                             else
                                 [ $"{operations.Length} durable operation(s) require reconciliation." ])

                    let planCount, planIssues =
                        match plansResult with
                        | Ok plans -> plans.Length, []
                        | Error error -> 0, [ error.Summary ]

                    let claimCount, knowledgeIssues =
                        match knowledgeResult with
                        | Ok graph -> graph.Claims.Count, []
                        | Error error -> 0, [ error.Summary ]

                    let lineageIssues =
                        match lineageResult with
                        | Ok lineage when lineage.Baseline.IsSome && lineage.Frontier.IsSome -> []
                        | Ok _ -> [ "Private Git lineage is missing its baseline or frontier ref." ]
                        | Error error -> [ error.Summary ]

                    let issues =
                        lineageIssues @ artifactIssues @ operationIssues @ planIssues @ knowledgeIssues

                    return
                        Ok
                            { RunId = runId
                              Healthy = List.isEmpty issues
                              ArtifactCount = artifactCount
                              PendingOperationCount = pendingCount
                              WorkPlanCount = planCount
                              KnowledgeClaimCount = claimCount
                              Issues = issues }
        }

    member _.Recover(runId: RunId, cancellationToken: CancellationToken) =
        async {
            match prepared, state with
            | Some _, _
            | _, Some _ ->
                return
                    Error(
                        HarnessError.create
                            "recovery.runtime_busy"
                            HarnessErrorCategory.Recovery
                            "Another run is already prepared or active in this runtime."
                    )
            | None, None ->
                match! journal.Initialize cancellationToken with
                | Error error -> return Error error
                | Ok() ->
                    let storedRunResult =
                        match SqliteStore.loadRuns sqlite with
                        | Error error -> Error error
                        | Ok runs ->
                            match runs |> List.tryFind (fun run -> run.Id = runId) with
                            | Some run -> Ok run
                            | None ->
                                Error(
                                    HarnessError.create
                                        "recovery.run_missing"
                                        HarnessErrorCategory.Recovery
                                        "The requested persisted run was not found."
                                )

                    match storedRunResult with
                    | Error error -> return Error error
                    | Ok storedRun ->
                        let configResult =
                            match storedRun.ConfigJson with
                            | Some json -> ConfigFile.parse json
                            | None -> Error [ "The persisted run has no configuration." ]

                        match configResult with
                        | Error errors ->
                            return
                                Error(
                                    HarnessError.create
                                        "recovery.config_invalid"
                                        HarnessErrorCategory.Recovery
                                        "The persisted run configuration cannot be restored."
                                    |> HarnessError.withDetail (String.concat " " errors)
                                )
                        | Ok config ->
                            match ProjectLock.tryAcquire (DataPaths.projectLock root config.SourcePath) with
                            | Error detail ->
                                return
                                    Error(
                                        HarnessError.create
                                            "recovery.locked"
                                            HarnessErrorCategory.Recovery
                                            "The source repository is locked by another run."
                                        |> HarnessError.withDetail detail
                                    )
                            | Ok runLock ->
                                activeLock <- Some runLock
                                let! repositoryResult = git.InspectSource config.SourcePath cancellationToken
                                let! codexResult = codex.Preflight codexExecutable cancellationToken

                                match repositoryResult, codexResult with
                                | Error error, _
                                | _, Error error ->
                                    releaseRunLock ()
                                    return Error error
                                | Ok repository, Ok codexReport ->
                                    match validateModel config codexReport with
                                    | Error error ->
                                        releaseRunLock ()
                                        return Error error
                                    | Ok() ->
                                        match! reconcilePendingOperations runId config cancellationToken with
                                        | Error error ->
                                            releaseRunLock ()
                                            return Error error
                                        | Ok() ->
                                            match SqliteStore.loadExperiments sqlite runId with
                                            | Error error ->
                                                releaseRunLock ()
                                                return Error error
                                            | Ok beforeRecovery ->
                                                for experiment in beforeRecovery do
                                                    if
                                                        experiment.Outcome = "Active"
                                                        || experiment.Outcome = "AwaitingReview"
                                                    then
                                                        do!
                                                            journalEvent
                                                                runId
                                                                (Some experiment.Id)
                                                                "Cancelled"
                                                                "Recovered an interrupted experiment; candidate evidence remains preserved."
                                                                cancellationToken

                                                let artifactResult = SqliteStore.loadArtifactsForRun sqlite runId

                                                match artifactResult with
                                                | Error error ->
                                                    releaseRunLock ()
                                                    return Error error
                                                | Ok artifacts ->
                                                    let integrityErrors =
                                                        artifacts
                                                        |> List.choose (fun artifact ->
                                                            match SqliteStore.verifyArtifact artifact with
                                                            | Ok() -> None
                                                            | Error detail -> Some detail)

                                                    if not (List.isEmpty integrityErrors) then
                                                        releaseRunLock ()

                                                        return
                                                            Error(
                                                                HarnessError.create
                                                                    "recovery.artifact_integrity"
                                                                    HarnessErrorCategory.Recovery
                                                                    "One or more persisted artifacts failed integrity verification."
                                                                |> HarnessError.withDetail (
                                                                    String.concat " " integrityErrors
                                                                )
                                                            )
                                                    else
                                                        let! snapshot =
                                                            loadEvolutionSnapshot storedRun cancellationToken

                                                        match
                                                            snapshot.Champion,
                                                            snapshot.Run.FrontierScore,
                                                            snapshot.Run.BaselineScore,
                                                            SqliteStore.loadExperiments sqlite runId,
                                                            SqliteStore.loadEvaluationsForRun sqlite runId,
                                                            SqliteStore.loadUsageForRun sqlite runId
                                                        with
                                                        | Some champion,
                                                          Some championScore,
                                                          Some baselineScore,
                                                          Ok experiments,
                                                          Ok evaluations,
                                                          Ok usages ->
                                                            let parsedEvaluations =
                                                                evaluations
                                                                |> List.sortBy _.CreatedAt
                                                                |> List.choose (fun stored ->
                                                                    parseStoredEvaluation stored.ResultJson
                                                                    |> Option.map (fun value ->
                                                                        stored.ExperimentId, value))

                                                            let baseline =
                                                                experiments
                                                                |> List.tryFind (fun experiment ->
                                                                    experiment.Sequence = 0)
                                                                |> Option.bind (fun experiment ->
                                                                    parsedEvaluations
                                                                    |> List.tryPick (fun (experimentId, evaluation) ->
                                                                        if experimentId = experiment.Id then
                                                                            Some evaluation
                                                                        else
                                                                            None))

                                                            match baseline with
                                                            | None ->
                                                                releaseRunLock ()

                                                                return
                                                                    Error(
                                                                        HarnessError.create
                                                                            "recovery.baseline_missing"
                                                                            HarnessErrorCategory.Recovery
                                                                            "The persisted baseline evaluation is missing."
                                                                        |> HarnessError.withDetail (
                                                                            let experimentDetails =
                                                                                experiments
                                                                                |> List.map (fun item ->
                                                                                    $"{ExperimentId.text item.Id}:{item.Sequence}:{item.Outcome}")
                                                                                |> String.concat ","

                                                                            let evaluationDetails =
                                                                                parsedEvaluations
                                                                                |> List.map (fst >> ExperimentId.text)
                                                                                |> String.concat ","

                                                                            $"Loaded experiments [{experimentDetails}] and parsed evaluations [{evaluationDetails}]."
                                                                        )
                                                                    )
                                                            | Some baselineEvaluation ->
                                                                let usage =
                                                                    usages
                                                                    |> List.choose _.Usage
                                                                    |> List.fold TokenUsage.add TokenUsage.zero

                                                                let usageKnown =
                                                                    usages
                                                                    |> List.forall (fun stored -> stored.Usage.IsSome)

                                                                let previousEvaluation =
                                                                    parsedEvaluations
                                                                    |> List.filter (fun (experimentId, _) ->
                                                                        experiments
                                                                        |> List.exists (fun experiment ->
                                                                            experiment.Id = experimentId
                                                                            && experiment.Sequence > 0))
                                                                    |> List.tryLast
                                                                    |> Option.map snd

                                                                let restoredState =
                                                                    { RunState.create
                                                                          runId
                                                                          config
                                                                          champion
                                                                          championScore
                                                                          DateTimeOffset.UtcNow with
                                                                        Status = Ready
                                                                        Attempted = snapshot.Run.AttemptCount
                                                                        AcceptedCount = snapshot.Run.AcceptedCount
                                                                        Usage = usage
                                                                        UsageKnown = usageKnown
                                                                        PreviousEvaluation = previousEvaluation
                                                                        ActiveHeads =
                                                                            if Set.isEmpty snapshot.ActiveHeads then
                                                                                Set.singleton champion
                                                                            else
                                                                                snapshot.ActiveHeads }

                                                                let report =
                                                                    { RunId = runId
                                                                      Repository = repository
                                                                      Codex = codexReport
                                                                      Baseline = baselineEvaluation
                                                                      BaselineScore = baselineScore
                                                                      DataDirectory = DataPaths.runRoot root runId }

                                                                prepared <- Some report
                                                                setState restoredState

                                                                do!
                                                                    journalEvent
                                                                        runId
                                                                        None
                                                                        "RunRecovered"
                                                                        (CommitOid.value champion)
                                                                        cancellationToken

                                                                publish
                                                                    "Recovered persisted run from its verified champion and active heads."
                                                                    None

                                                                return Ok report
                                                        | _ ->
                                                            releaseRunLock ()

                                                            return
                                                                Error(
                                                                    HarnessError.create
                                                                        "recovery.state_incomplete"
                                                                        HarnessErrorCategory.Recovery
                                                                        "The persisted run does not contain enough verified state to resume."
                                                                )
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
                                        | Error error -> abortPersistence error
                                        | Ok() -> ()

                                        let manifest = reproducibilityManifest runId validated repository codexReport

                                        match!
                                            SqliteStore.saveReproducibilityManifest
                                                sqlite
                                                runId
                                                manifest
                                                cancellationToken
                                        with
                                        | Error error -> abortPersistence error
                                        | Ok _ -> ()

                                        let baselineExperiment = ExperimentId.create ()

                                        match!
                                            SqliteStore.beginExperiment
                                                sqlite
                                                runId
                                                baselineExperiment
                                                0
                                                None
                                                "Baseline"
                                                cancellationToken
                                        with
                                        | Error error -> abortPersistence error
                                        | Ok() -> ()

                                        match!
                                            SqliteStore.updateExperimentCandidate
                                                sqlite
                                                baselineExperiment
                                                validated.BaseCommit
                                                cancellationToken
                                        with
                                        | Error error -> abortPersistence error
                                        | Ok() -> notifyEvolution runId

                                        match!
                                            git.PrepareCandidate
                                                runId
                                                baselineExperiment
                                                (expansionParents validated.BaseCommit)
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
                                                    runId
                                                    baselineExperiment
                                                    validated.Evaluator
                                                    baselineWorkspace.GenerationPath
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
                                                | Error error -> abortPersistence error

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
                                                    | Ok(frontier, frontierScore, seedHeads) ->
                                                        match!
                                                            SqliteStore.replaceActiveHeads
                                                                sqlite
                                                                runId
                                                                -1
                                                                seedHeads
                                                                cancellationToken
                                                        with
                                                        | Error error -> abortPersistence error
                                                        | Ok() -> ()

                                                        match!
                                                            SqliteStore.updateRunStatus
                                                                sqlite
                                                                runId
                                                                "Ready"
                                                                cancellationToken
                                                        with
                                                        | Error error -> abortPersistence error
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
                                                            RunState.createWithHeads
                                                                runId
                                                                validated
                                                                frontier
                                                                frontierScore
                                                                seedHeads
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
        |> fun operation ->
            async {
                let! outcome = Async.Catch operation

                match outcome with
                | Choice1Of2 result -> return result
                | Choice2Of2(RuntimePersistenceAbort error) -> return Error error
                | Choice2Of2 exceptionValue ->
                    releaseRunLock ()

                    return
                        Error(
                            HarnessError.create
                                "runtime.prepare_unexpected"
                                HarnessErrorCategory.Recovery
                                "Run preparation failed unexpectedly."
                            |> HarnessError.withDetail exceptionValue.Message
                        )
            }

    member _.Start() =
        match prepared, tryCurrent () with
        | Some _, Some current when
            current.Status = Ready
            && Evaluation.targetReached current.Config.Metric current.ChampionScore
            ->
            setState
                { current with
                    Status = Completed "Metric target already reached by the prepared frontier." }

            publish "Run completed without another experiment because the metric target is already reached." None
            Ok current.Id
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
                            dispatch (ReviewAccepted active.Id) |> ignore

                            match!
                                promoteCandidate
                                    current.Id
                                    active.Id
                                    active.ChampionAtStart
                                    candidate
                                    metric
                                    summary
                                    "AcceptedAfterReview"
                                    active.Kind
                                    active.ChampionAtStart
                            with
                            | Error error ->
                                publish $"Reviewed candidate acceptance failed safely: {error.Summary}" (Some active.Id)

                                return Error error
                            | Ok() ->
                                publish
                                    $"Accepted reviewed strict improvement: {current.ChampionScore} → {metric}."
                                    (Some active.Id)

                                startWorkerIfReady ()
                                return Ok()
                        else
                            dispatch (ReviewRejected active.Id) |> ignore

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
                        | Maximize -> (current.ChampionScore - value) / abs value * 100M
                        | Minimize -> (value - current.ChampionScore) / abs value * 100M

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
            runCancellation
            |> Option.iter (fun cancellation ->
                try
                    cancellation.Cancel()
                    cancellation.Dispose()
                with :? ObjectDisposedException ->
                    ())

            runCancellation <- None
            releaseRunLock ()
