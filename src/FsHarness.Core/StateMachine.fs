namespace FsHarness.Core

open System

type ExperimentPhase =
    | PreparingWorktree
    | Generating
    | SnapshotPrepared
    | Evaluating
    | Deciding
    | AcceptPending
    | AwaitingReview

type ExperimentOutcome =
    | Accepted of decimal
    | RejectedNotBetter of RejectionReason
    | RejectedByUser
    | Failed of HarnessError
    | InvalidProtectedPath of string list
    | Cancelled
    | Crashed of HarnessError

type ActiveExperiment =
    { Id: ExperimentId
      Sequence: int
      Parent: CommitOid
      Phase: ExperimentPhase
      Candidate: CommitOid option
      ThreadId: string option
      Summary: ExperimentSummary option
      Evaluation: EvaluationResult option
      Usage: TokenUsage option
      StartedAt: DateTimeOffset }

type RunStatus =
    | Ready
    | Running
    | PauseAfterCurrent
    | Paused of string
    | Stopping
    | Completed of string
    | RecoveryRequired of HarnessError

type RunState =
    { Id: RunId
      Config: HarnessConfig
      Status: RunStatus
      Frontier: CommitOid
      FrontierScore: decimal
      Current: ActiveExperiment option
      Attempted: int
      AcceptedCount: int
      ConsecutiveNonImprovements: int
      ConsecutiveFailures: int
      Usage: TokenUsage
      UsageKnown: bool
      PreviousEvaluation: EvaluationResult option
      StartedAt: DateTimeOffset }

type RunEvent =
    | StartRequested of ExperimentId * DateTimeOffset
    | WorktreePrepared
    | GenerationCompleted of threadId: string * usage: TokenUsage option * summary: ExperimentSummary
    | CandidateCaptured of CommitOid
    | ProtectedPathDetected of string list
    | EvaluationCompleted of EvaluationResult
    | FrontierAdvanced
    | ReviewAccepted
    | ReviewRejected
    | ExperimentFailed of HarnessError
    | PauseRequested
    | ResumeRequested
    | StopRequested
    | CancellationCompleted

type RunEffect =
    | PrepareCandidate of ExperimentId * CommitOid
    | LaunchCodex of ExperimentId
    | CaptureCandidate of ExperimentId
    | RunEvaluator of ExperimentId * CommitOid
    | PersistAccepted of ExperimentId * CommitOid * expectedParent: CommitOid
    | PersistRejected of ExperimentId * ExperimentOutcome
    | PublishState
    | ScheduleNext
    | CancelActiveProcess of ExperimentId

[<RequireQualifiedAccess>]
module RunState =
    let create runId config frontier frontierScore startedAt =
        { Id = runId
          Config = config
          Status = Ready
          Frontier = frontier
          FrontierScore = frontierScore
          Current = None
          Attempted = 0
          AcceptedCount = 0
          ConsecutiveNonImprovements = 0
          ConsecutiveFailures = 0
          Usage = TokenUsage.zero
          UsageKnown = true
          PreviousEvaluation = None
          StartedAt = startedAt }

    let private budgetStopReason now state =
        if state.Attempted >= state.Config.Budgets.MaxExperiments then
            Some "Experiment budget exhausted."
        elif TokenUsage.rawTotal state.Usage >= state.Config.Budgets.MaxRawTokens then
            Some "Raw-token budget exhausted."
        elif now - state.StartedAt >= state.Config.Budgets.MaxDuration then
            Some "Wall-clock budget exhausted."
        elif
            state.ConsecutiveNonImprovements
            >= state.Config.Budgets.MaxConsecutiveNonImprovements
        then
            Some "Non-improvement limit reached."
        elif state.ConsecutiveFailures >= state.Config.Budgets.MaxConsecutiveFailures then
            Some "Failure limit reached."
        else
            None

    let private finishExperiment now outcome state =
        let previousEvaluation =
            state.Current
            |> Option.bind _.Evaluation
            |> Option.orElse state.PreviousEvaluation

        let nextState =
            match outcome with
            | Accepted score ->
                { state with
                    FrontierScore = score
                    AcceptedCount = state.AcceptedCount + 1
                    ConsecutiveFailures = 0
                    ConsecutiveNonImprovements = 0
                    PreviousEvaluation = previousEvaluation
                    Current = None }
            | RejectedNotBetter _
            | RejectedByUser ->
                { state with
                    ConsecutiveFailures = 0
                    ConsecutiveNonImprovements = state.ConsecutiveNonImprovements + 1
                    PreviousEvaluation = previousEvaluation
                    Current = None }
            | Failed _
            | InvalidProtectedPath _
            | Cancelled
            | Crashed _ ->
                { state with
                    ConsecutiveFailures = state.ConsecutiveFailures + 1
                    PreviousEvaluation = previousEvaluation
                    Current = None }

        match nextState.Status, budgetStopReason now nextState with
        | PauseAfterCurrent, _ ->
            { nextState with
                Status = Paused "Pause requested." },
            [ PublishState ]
        | Stopping, _ ->
            { nextState with
                Status = Completed "Stopped by user." },
            [ PublishState ]
        | _, Some reason ->
            { nextState with
                Status = Completed reason },
            [ PublishState ]
        | _, None -> { nextState with Status = Ready }, [ PublishState; ScheduleNext ]

    let transition now event state =
        match event, state.Status, state.Current with
        | StartRequested(experimentId, startedAt), Ready, None ->
            let active =
                { Id = experimentId
                  Sequence = state.Attempted + 1
                  Parent = state.Frontier
                  Phase = PreparingWorktree
                  Candidate = None
                  ThreadId = None
                  Summary = None
                  Evaluation = None
                  Usage = None
                  StartedAt = startedAt }

            { state with
                Status = Running
                Current = Some active
                Attempted = state.Attempted + 1 },
            [ PrepareCandidate(experimentId, state.Frontier); PublishState ]
        | WorktreePrepared, Running, Some active when active.Phase = PreparingWorktree ->
            { state with
                Current = Some { active with Phase = Generating } },
            [ LaunchCodex active.Id; PublishState ]
        | GenerationCompleted(threadId, usage, summary), (Running | PauseAfterCurrent), Some active when
            active.Phase = Generating
            ->
            let nextUsage, usageKnown =
                match usage with
                | Some value -> TokenUsage.add state.Usage value, state.UsageKnown
                | None -> state.Usage, false

            let nextStatus =
                match usage with
                | Some _ -> state.Status
                | None -> PauseAfterCurrent

            { state with
                Status = nextStatus
                Usage = nextUsage
                UsageKnown = usageKnown
                Current =
                    Some
                        { active with
                            Phase = SnapshotPrepared
                            ThreadId = Some threadId
                            Summary = Some summary
                            Usage = usage } },
            [ CaptureCandidate active.Id; PublishState ]
        | CandidateCaptured candidate, (Running | PauseAfterCurrent), Some active when active.Phase = SnapshotPrepared ->
            { state with
                Current =
                    Some
                        { active with
                            Phase = Evaluating
                            Candidate = Some candidate } },
            [ RunEvaluator(active.Id, candidate); PublishState ]
        | ProtectedPathDetected paths, _, Some active ->
            finishExperiment now (InvalidProtectedPath paths) state
            |> fun (next, effects) -> next, PersistRejected(active.Id, InvalidProtectedPath paths) :: effects
        | EvaluationCompleted evaluation, (Running | PauseAfterCurrent), Some active when active.Phase = Evaluating ->
            let evaluatedState =
                { state with
                    Current =
                        Some
                            { active with
                                Phase = Deciding
                                Evaluation = Some evaluation } }

            match
                Evaluation.decide
                    state.Config.Metric
                    state.Config.Evaluator.RequiredConstraints
                    state.FrontierScore
                    evaluation
            with
            | Rejected reason ->
                finishExperiment now (RejectedNotBetter reason) evaluatedState
                |> fun (next, effects) -> next, PersistRejected(active.Id, RejectedNotBetter reason) :: effects
            | StrictImprovement score ->
                match state.Config.PromotionMode, active.Candidate with
                | AutoWhenStrictlyBetter, Some candidate ->
                    { evaluatedState with
                        Current =
                            Some
                                { active with
                                    Phase = AcceptPending
                                    Evaluation = Some evaluation } },
                    [ PersistAccepted(active.Id, candidate, active.Parent); PublishState ]
                | ReviewStrictWinners, _ ->
                    { evaluatedState with
                        Current =
                            Some
                                { active with
                                    Phase = AwaitingReview
                                    Evaluation = Some evaluation } },
                    [ PublishState ]
                | _, None ->
                    let error =
                        HarnessError.create "state.candidate_missing" Recovery "Candidate commit missing at decision."

                    { state with
                        Status = RecoveryRequired error },
                    [ PublishState ]
        | FrontierAdvanced, _, Some active when active.Phase = AcceptPending ->
            match active.Candidate, active.Evaluation with
            | Some candidate, Some evaluation ->
                let score = evaluation.Metrics[state.Config.Metric.Name]
                let updated = { state with Frontier = candidate }
                finishExperiment now (Accepted score) updated
            | _ ->
                let error =
                    HarnessError.create "state.accept_incomplete" Recovery "Acceptance state is incomplete."

                { state with
                    Status = RecoveryRequired error },
                [ PublishState ]
        | ReviewAccepted, _, Some active when active.Phase = AwaitingReview ->
            match active.Candidate with
            | Some candidate ->
                { state with
                    Current = Some { active with Phase = AcceptPending } },
                [ PersistAccepted(active.Id, candidate, active.Parent); PublishState ]
            | None -> state, []
        | ReviewRejected, _, Some active when active.Phase = AwaitingReview ->
            finishExperiment now RejectedByUser state
            |> fun (next, effects) -> next, PersistRejected(active.Id, RejectedByUser) :: effects
        | ExperimentFailed error, _, Some active ->
            finishExperiment now (Failed error) state
            |> fun (next, effects) -> next, PersistRejected(active.Id, Failed error) :: effects
        | PauseRequested, Running, _ ->
            { state with
                Status = PauseAfterCurrent },
            [ PublishState ]
        | ResumeRequested, Paused _, None -> { state with Status = Ready }, [ ScheduleNext; PublishState ]
        | StopRequested, Running, Some active
        | StopRequested, PauseAfterCurrent, Some active ->
            { state with Status = Stopping }, [ CancelActiveProcess active.Id; PublishState ]
        | CancellationCompleted, Stopping, Some active ->
            finishExperiment now Cancelled state
            |> fun (next, effects) -> next, PersistRejected(active.Id, Cancelled) :: effects
        | StopRequested, Ready, None
        | StopRequested, Paused _, None ->
            { state with
                Status = Completed "Stopped by user." },
            [ PublishState ]
        | _ -> state, []
