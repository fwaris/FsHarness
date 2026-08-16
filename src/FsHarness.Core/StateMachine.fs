namespace FsHarness.Core

open System

type ExperimentPhase =
    | PreparingWorktree
    | Generating
    | SnapshotPrepared
    | Evaluating
    | Deciding
    | AcceptPending
    | Promoting
    | AwaitingReview

type ExperimentOutcome =
    | Accepted of decimal
    | RejectedNotBetter of RejectionReason
    | RejectedByUser
    | Failed of HarnessError
    | InvalidProtectedPath of string list
    | InconclusiveEvaluation of string
    | Cancelled
    | Crashed of HarnessError

type ActiveExperiment =
    { Id: ExperimentId
      Sequence: int
      Kind: ExperimentKind
      Parents: ExperimentParent list
      ChampionAtStart: CommitOid
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
      Champion: CommitOid
      ChampionScore: decimal
      ActiveHeads: Set<CommitOid>
      Current: ActiveExperiment option
      Attempted: int
      AcceptedCount: int
      ConsecutiveNonImprovements: int
      ConsecutiveFailures: int
      Usage: TokenUsage
      UsageAtFirstAcceptance: TokenUsage option
      UsageKnown: bool
      PreviousEvaluation: EvaluationResult option
      LastExperimentKind: ExperimentKind option
      StartedAt: DateTimeOffset }

type RunEvent =
    | StartRequested of ExperimentId * DateTimeOffset
    | GraphExperimentRequested of
        ExperimentId *
        ExperimentKind *
        ExperimentParent list *
        championAtStart: CommitOid *
        DateTimeOffset
    | WorktreePrepared of ExperimentId
    | GenerationCompleted of ExperimentId * threadId: string * usage: TokenUsage option * summary: ExperimentSummary
    | DuplicateCandidateSkipped of
        ExperimentId *
        fingerprint: string *
        matchingExperiment: ExperimentId *
        usage: TokenUsage option
    | CandidateCaptured of ExperimentId * CommitOid
    | ProtectedPathDetected of ExperimentId * string list
    | EvaluationCompleted of ExperimentId * EvaluationResult
    | EvaluationInconclusive of ExperimentId * string
    | PromotionStarted of ExperimentId
    | ChampionAdvanced of ExperimentId
    | ReviewAccepted of ExperimentId
    | ReviewRejected of ExperimentId
    | ExperimentFailed of ExperimentId * HarnessError
    | PauseRequested
    | ResumeRequested
    | StopRequested
    | CancellationCompleted of ExperimentId

type RunEffect =
    | PrepareCandidate of ExperimentId * ExperimentParent list * championAtStart: CommitOid
    | LaunchCodex of ExperimentId
    | CaptureCandidate of ExperimentId
    | RunEvaluator of ExperimentId * CommitOid
    | PersistAccepted of ExperimentId * CommitOid * expectedChampion: CommitOid
    | PersistRejected of ExperimentId * ExperimentOutcome
    | PublishState
    | ScheduleNext
    | CancelActiveProcess of ExperimentId

[<RequireQualifiedAccess>]
module RunState =
    let create runId config champion championScore startedAt =
        { Id = runId
          Config = config
          Status = Ready
          Champion = champion
          ChampionScore = championScore
          ActiveHeads = Set.singleton champion
          Current = None
          Attempted = 0
          AcceptedCount = 0
          ConsecutiveNonImprovements = 0
          ConsecutiveFailures = 0
          Usage = TokenUsage.zero
          UsageAtFirstAcceptance = None
          UsageKnown = true
          PreviousEvaluation = None
          LastExperimentKind = None
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
            && (state.LastExperimentKind = Some ExperimentKind.Synthesis
                || state.ConsecutiveNonImprovements
                   >= state.Config.Budgets.MaxConsecutiveNonImprovements
                      + state.Config.GraphSearch.BeamWidth)
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

        let lastExperimentKind = state.Current |> Option.map _.Kind

        let nextState =
            match outcome with
            | Accepted score ->
                { state with
                    ChampionScore = score
                    AcceptedCount = state.AcceptedCount + 1
                    UsageAtFirstAcceptance = state.UsageAtFirstAcceptance |> Option.orElse (Some state.Usage)
                    ConsecutiveFailures = 0
                    ConsecutiveNonImprovements = 0
                    PreviousEvaluation = previousEvaluation
                    LastExperimentKind = lastExperimentKind
                    Current = None }
            | RejectedNotBetter reason ->
                let retainCandidate =
                    match reason with
                    | ConstraintFailed _
                    | MetricMissing _ -> false
                    | NotStrictlyBetter _ -> true

                let activeHeads =
                    if retainCandidate then
                        state.Current
                        |> Option.bind _.Candidate
                        |> Option.map (fun candidate -> Set.add candidate state.ActiveHeads)
                        |> Option.defaultValue state.ActiveHeads
                    else
                        state.ActiveHeads

                { state with
                    ConsecutiveFailures = 0
                    ConsecutiveNonImprovements = state.ConsecutiveNonImprovements + 1
                    PreviousEvaluation = previousEvaluation
                    ActiveHeads = activeHeads
                    LastExperimentKind = lastExperimentKind
                    Current = None }
            | RejectedByUser ->
                { state with
                    ConsecutiveFailures = 0
                    ConsecutiveNonImprovements = state.ConsecutiveNonImprovements + 1
                    PreviousEvaluation = previousEvaluation
                    LastExperimentKind = lastExperimentKind
                    Current = None }
            | Failed _
            | InvalidProtectedPath _
            | Cancelled
            | Crashed _ ->
                { state with
                    ConsecutiveFailures = state.ConsecutiveFailures + 1
                    PreviousEvaluation = previousEvaluation
                    LastExperimentKind = lastExperimentKind
                    Current = None }
            | InconclusiveEvaluation _ ->
                { state with
                    ConsecutiveFailures = 0
                    PreviousEvaluation = previousEvaluation
                    LastExperimentKind = lastExperimentKind
                    Current = None }

        match
            nextState.Status,
            Evaluation.targetReached nextState.Config.Metric nextState.ChampionScore,
            budgetStopReason now nextState
        with
        | PauseAfterCurrent, _, _ ->
            { nextState with
                Status = Paused "Pause requested." },
            [ PublishState ]
        | Stopping, _, _ ->
            { nextState with
                Status = Completed "Stopped by user." },
            [ PublishState ]
        | _, true, _ ->
            { nextState with
                Status = Completed "Metric target reached." },
            [ PublishState ]
        | _, _, Some reason ->
            { nextState with
                Status = Completed reason },
            [ PublishState ]
        | _, _, None -> { nextState with Status = Ready }, [ PublishState; ScheduleNext ]

    let private skipDuplicate now usage state =
        let nextUsage, usageKnown =
            match usage with
            | Some value -> TokenUsage.add state.Usage value, state.UsageKnown
            | None -> state.Usage, false

        let nextState =
            { state with
                Current = None
                Attempted = max 0 (state.Attempted - 1)
                Usage = nextUsage
                UsageKnown = usageKnown }

        match state.Status, usage, budgetStopReason now nextState with
        | Stopping, _, _ ->
            { nextState with
                Status = Completed "Stopped by user." },
            [ PublishState ]
        | PauseAfterCurrent, _, _ ->
            { nextState with
                Status = Paused "Pause requested." },
            [ PublishState ]
        | _, None, _ ->
            { nextState with
                Status = Paused "Terminal token usage is unavailable." },
            [ PublishState ]
        | _, _, Some reason ->
            { nextState with
                Status = Completed reason },
            [ PublishState ]
        | _ -> { nextState with Status = Ready }, [ PublishState; ScheduleNext ]

    let rec transition now event state =
        match event, state.Status, state.Current with
        | StartRequested(experimentId, startedAt), Ready, None ->
            transition
                now
                (GraphExperimentRequested(
                    experimentId,
                    ExperimentKind.Expansion,
                    [ { Commit = state.Champion
                        Role = ExperimentParentRole.Primary } ],
                    state.Champion,
                    startedAt
                ))
                state
        | GraphExperimentRequested(experimentId, kind, parents, championAtStart, startedAt), Ready, None ->
            match Evaluation.targetReached state.Config.Metric state.ChampionScore, budgetStopReason now state with
            | true, _ ->
                { state with
                    Status = Completed "Metric target reached." },
                [ PublishState ]
            | _, Some reason -> { state with Status = Completed reason }, [ PublishState ]
            | _ ->
                let active =
                    { Id = experimentId
                      Sequence = state.Attempted + 1
                      Kind = kind
                      Parents = parents
                      ChampionAtStart = championAtStart
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
                [ PrepareCandidate(experimentId, parents, championAtStart); PublishState ]
        | WorktreePrepared experimentId, (Running | PauseAfterCurrent), Some active when
            active.Id = experimentId && active.Phase = PreparingWorktree
            ->
            { state with
                Current = Some { active with Phase = Generating } },
            [ LaunchCodex active.Id; PublishState ]
        | DuplicateCandidateSkipped(experimentId, _, _, usage), (Running | PauseAfterCurrent | Stopping), Some active when
            active.Id = experimentId && active.Phase = Generating
            ->
            skipDuplicate now usage state
        | GenerationCompleted(experimentId, threadId, usage, summary), (Running | PauseAfterCurrent), Some active when
            active.Id = experimentId && active.Phase = Generating
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
        | CandidateCaptured(experimentId, candidate), (Running | PauseAfterCurrent), Some active when
            active.Id = experimentId && active.Phase = SnapshotPrepared
            ->
            { state with
                Current =
                    Some
                        { active with
                            Phase = Evaluating
                            Candidate = Some candidate } },
            [ RunEvaluator(active.Id, candidate); PublishState ]
        | ProtectedPathDetected(experimentId, paths), (Running | PauseAfterCurrent | Stopping), Some active when
            active.Id = experimentId
            ->
            finishExperiment now (InvalidProtectedPath paths) state
            |> fun (next, effects) -> next, PersistRejected(active.Id, InvalidProtectedPath paths) :: effects
        | EvaluationInconclusive(experimentId, reason), _, Some active when
            active.Id = experimentId && active.Phase = Evaluating
            ->
            let next, _ = finishExperiment now (InconclusiveEvaluation reason) state

            { next with Status = Paused reason },
            [ PersistRejected(active.Id, InconclusiveEvaluation reason); PublishState ]
        | EvaluationCompleted(experimentId, evaluation), (Running | PauseAfterCurrent), Some active when
            active.Id = experimentId && active.Phase = Evaluating
            ->
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
                    state.ChampionScore
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
                    [ PersistAccepted(active.Id, candidate, active.ChampionAtStart); PublishState ]
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
        | PromotionStarted experimentId, (Running | PauseAfterCurrent), Some active when
            active.Id = experimentId && active.Phase = AcceptPending
            ->
            { state with
                Current = Some { active with Phase = Promoting } },
            [ PublishState ]
        | ChampionAdvanced experimentId, _, Some active when active.Id = experimentId && active.Phase = Promoting ->
            match active.Candidate, active.Evaluation with
            | Some candidate, Some evaluation ->
                let score = evaluation.Metrics[state.Config.Metric.Name]

                let updated =
                    { state with
                        Champion = candidate
                        ActiveHeads = Set.add candidate state.ActiveHeads }

                finishExperiment now (Accepted score) updated
            | _ ->
                let error =
                    HarnessError.create "state.accept_incomplete" Recovery "Acceptance state is incomplete."

                { state with
                    Status = RecoveryRequired error },
                [ PublishState ]
        | ReviewAccepted experimentId, (Running | PauseAfterCurrent), Some active when
            active.Id = experimentId && active.Phase = AwaitingReview
            ->
            match active.Candidate with
            | Some candidate ->
                { state with
                    Current = Some { active with Phase = AcceptPending } },
                [ PersistAccepted(active.Id, candidate, active.ChampionAtStart); PublishState ]
            | None -> state, []
        | ReviewRejected experimentId, (Running | PauseAfterCurrent), Some active when
            active.Id = experimentId && active.Phase = AwaitingReview
            ->
            finishExperiment now RejectedByUser state
            |> fun (next, effects) -> next, PersistRejected(active.Id, RejectedByUser) :: effects
        | ExperimentFailed(experimentId, error), (Running | PauseAfterCurrent | Stopping), Some active when
            active.Id = experimentId
            ->
            finishExperiment now (Failed error) state
            |> fun (next, effects) -> next, PersistRejected(active.Id, Failed error) :: effects
        | PauseRequested, Running, _ ->
            { state with
                Status = PauseAfterCurrent },
            [ PublishState ]
        | ResumeRequested, Paused _, None when state.UsageKnown ->
            { state with Status = Ready }, [ ScheduleNext; PublishState ]
        | StopRequested, Running, Some active
        | StopRequested, PauseAfterCurrent, Some active ->
            { state with Status = Stopping }, [ CancelActiveProcess active.Id; PublishState ]
        | CancellationCompleted experimentId, Stopping, Some active when active.Id = experimentId ->
            finishExperiment now Cancelled state
            |> fun (next, effects) -> next, PersistRejected(active.Id, Cancelled) :: effects
        | StopRequested, Ready, None
        | StopRequested, Paused _, None ->
            { state with
                Status = Completed "Stopped by user." },
            [ PublishState ]
        | _ -> state, []
