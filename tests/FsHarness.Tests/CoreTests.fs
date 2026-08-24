namespace FsHarness.Tests

open System
open FsHarness.Core
open Xunit

module private Fixtures =
    let evaluator =
        { Executable = "fake-evaluator"
          Arguments = []
          WorkingDirectory = "."
          Timeout = TimeSpan.FromSeconds 5.0
          RequiredConstraints = [ "build"; "tests" ]
          MaxInconclusiveRetries = 2
          MaxInfrastructureRetries = 0
          InfrastructureRetryDelay = TimeSpan.FromMilliseconds 10.0 }

    let metric =
        { Name = "primary"
          Direction = Maximize
          MinDelta = 0.5M
          Target = None
          Comparison = RetainedScore }

    let config =
        { SchemaVersion = HarnessConfig.currentSchemaVersion
          SourcePath = "/tmp/source"
          BaseCommit = CommitOid.create (String('a', 40))
          Objective = "Improve the fixture."
          EditablePaths = [ "src/**" ]
          SeedPatches = []
          Evaluator = evaluator
          Metric = metric
          Model = Defaults.model
          PromptProfile = Defaults.promptProfile
          GraphSearch = Defaults.graphSearch
          Budgets = Defaults.budgets
          PromotionMode = AutoWhenStrictlyBetter }

    let summary =
        { HypothesisFamily = "fixture"
          Hypothesis = "One change"
          ChangeSummary = "Changed one file"
          ExpectedEffect = "Metric rises"
          ValidationNotes = [ "Checked" ]
          ReusableLesson = "Keep it small" }

module TokenUsageTests =
    [<Fact>]
    let ``cached and reasoning tokens are not added twice`` () =
        let usage =
            { InputTokens = 100L
              CachedInputTokens = 80L
              OutputTokens = 40L
              ReasoningOutputTokens = 30L }

        Assert.Equal(140L, TokenUsage.rawTotal usage)
        Assert.Equal(60L, TokenUsage.uncachedTotal usage)
        Assert.Equal(10L, TokenUsage.visibleOutput usage)

    [<Fact>]
    let ``normalization clamps subset counters`` () =
        let normalized =
            TokenUsage.normalize
                { InputTokens = 10L
                  CachedInputTokens = 50L
                  OutputTokens = -1L
                  ReasoningOutputTokens = 20L }

        Assert.Equal(10L, normalized.CachedInputTokens)
        Assert.Equal(0L, normalized.OutputTokens)
        Assert.Equal(0L, normalized.ReasoningOutputTokens)

module CodexWritePolicyTests =
    [<Fact>]
    let ``missing policy defaults to unrestricted`` () =
        Assert.Equal(Ok CodexWritePolicy.Unrestricted, CodexWritePolicy.parse None)

    [<Theory>]
    [<InlineData("unrestricted")>]
    [<InlineData("UNRESTRICTED")>]
    let ``unrestricted aliases parse case insensitively`` value =
        Assert.Equal(Ok CodexWritePolicy.Unrestricted, CodexWritePolicy.parse (Some value))

    [<Fact>]
    let ``sandbox policies parse explicitly`` () =
        Assert.Equal(Ok CodexWritePolicy.WorkspaceWrite, CodexWritePolicy.parse (Some "workspace-write"))

        Assert.Equal(Ok CodexWritePolicy.ReadOnly, CodexWritePolicy.parse (Some "read-only"))

    [<Fact>]
    let ``unknown policy is rejected`` () =
        match CodexWritePolicy.parse (Some "typo") with
        | Ok _ -> Assert.Fail "An unknown write policy must not silently become read-only."
        | Error detail -> Assert.Contains("FSHARNESS_CODEX_WRITE_POLICY", detail)

module EvaluationTests =
    let private result score constraints =
        { SchemaVersion = 1
          Status = EvaluationStatus.Complete
          Constraints = constraints
          Metrics = Map [ "primary", score ]
          Summary = "fixture"
          Evidence = [] }

    [<Fact>]
    let ``strict improvement must exceed frontier and minimum delta`` () =
        let decision =
            Evaluation.decide
                Fixtures.metric
                [ "build"; "tests" ]
                10M
                (result 10.5M (Map [ "build", true; "tests", true ]))

        Assert.Equal(StrictImprovement 10.5M, decision)

    [<Fact>]
    let ``equal score is rejected even with zero delta`` () =
        let metric = { Fixtures.metric with MinDelta = 0M }

        let decision =
            Evaluation.decide metric [ "build" ] 10M (result 10M (Map [ "build", true ]))

        match decision with
        | Rejected(NotStrictlyBetter _) -> ()
        | other -> Assert.Fail $"Unexpected decision: {other}"

    [<Fact>]
    let ``constraint failure always wins over metric`` () =
        let decision =
            Evaluation.decide
                Fixtures.metric
                [ "build"; "tests" ]
                10M
                (result 99M (Map [ "build", true; "tests", false ]))

        Assert.Equal(Rejected(ConstraintFailed [ "tests" ]), decision)

module EvolutionTests =
    let private node id sequence outcome metric parent =
        { Id = ExperimentNode id
          Kind = EvolutionNodeKind.Candidate
          Sequence = sequence
          Parent = parent
          Commit = Some(CommitOid.create (String(char (98 + sequence), 40)))
          Outcome = outcome
          Metric = metric
          RetainedScore = None
          Summary = None
          EvaluationSummary = None
          Usage = None
          StartedAt = DateTimeOffset.UtcNow
          UpdatedAt = DateTimeOffset.UtcNow
          Label = string sequence }

    [<Fact>]
    let ``score points advance only on accepted candidates`` () =
        let first =
            node (ExperimentId.create ()) 1 EvolutionOutcome.Accepted (Some 11M) None

        let second =
            node (ExperimentId.create ()) 2 (EvolutionOutcome.Rejected "not better") (Some 10M) first.Commit

        let points = Evolution.scorePoints Maximize (Some 10M) [ first; second ]

        Assert.Equal<decimal option list>([ Some 10M; Some 11M; Some 11M ], points |> List.map _.RetainedScore)

    [<Fact>]
    let ``equal metric range still produces usable score points`` () =
        let candidate =
            node (ExperimentId.create ()) 1 EvolutionOutcome.Accepted (Some 10M) None

        let points = Evolution.scorePoints Minimize (Some 10M) [ candidate ]
        Assert.Equal(2, points.Length)

    [<Fact>]
    let ``paired metric compares candidate against evaluator frontier from same cycle`` () =
        let pairedMetric =
            { Fixtures.metric with
                Name = "candidate_speed_index"
                MinDelta = 2M
                Comparison = EvaluationMetric "frontier_speed_index" }

        let evaluation =
            { SchemaVersion = 2
              Status = EvaluationStatus.Complete
              Constraints = Map [ "build", true; "tests", true ]
              Metrics = Map [ "candidate_speed_index", 102.25M; "frontier_speed_index", 100M ]
              Summary = "paired"
              Evidence = [] }

        let decision = Evaluation.decide pairedMetric [ "build"; "tests" ] 999M evaluation
        Assert.Equal(StrictImprovement 102.25M, decision)

module PromptTests =
    [<Fact>]
    let ``memory count and character budget are bounded`` () =
        let memories =
            [ 1..20 ]
            |> List.map (fun _ ->
                { ExperimentId = ExperimentId.create ()
                  Outcome = String('x', 3_000)
                  Metric = Some 1M
                  Summary = Fixtures.summary })

        let prompt =
            Prompt.build
                { Objective = "Improve"
                  EditablePaths = [ "src/**" ]
                  ParentCommit = None
                  ChampionCommit = None
                  ChampionScore = 1M
                  Metric = Fixtures.metric
                  Profile = Defaults.promptProfile
                  PreviousEvaluation = None
                  Memories = memories
                  GraphContext = None }

        Assert.True(prompt.Length < 8_500, $"Prompt was unexpectedly large: {prompt.Length}")
        Assert.Contains("Keep tool output bounded", prompt)

    [<Fact>]
    let ``compact profile limits memories and evaluator findings`` () =
        let profile =
            { MaxMemoryCount = 2
              MaxMemoryCharacters = 2_000
              MaxEvaluationFindings = 3
              MaxEvaluationCharacters = 1_000 }

        let memories =
            [ 1..4 ]
            |> List.map (fun index ->
                { ExperimentId = ExperimentId.create ()
                  Outcome = $"memory-{index}"
                  Metric = Some(decimal index)
                  Summary = Fixtures.summary })

        let evaluation =
            { SchemaVersion = 2
              Status = EvaluationStatus.Complete
              Constraints = Map [ "a", false; "b", false; "c", true; "d", true ]
              Metrics = Map [ "one", 1M; "two", 2M ]
              Summary = "compact"
              Evidence = [] }

        let prompt =
            Prompt.build
                { Objective = "Improve"
                  EditablePaths = [ "src/**" ]
                  ParentCommit = None
                  ChampionCommit = None
                  ChampionScore = 1M
                  Metric = Fixtures.metric
                  Profile = profile
                  PreviousEvaluation = Some evaluation
                  Memories = memories
                  GraphContext = None }

        Assert.Contains("memory-1", prompt)
        Assert.Contains("memory-2", prompt)
        Assert.DoesNotContain("memory-3", prompt)
        Assert.Contains("constraint a=False", prompt)
        Assert.Contains("constraint b=False", prompt)
        Assert.Contains("constraint c=True", prompt)
        Assert.DoesNotContain("constraint d=True", prompt)
        Assert.DoesNotContain("metric one=1", prompt)

module StateMachineTests =
    [<Fact>]
    let ``explicit active heads are retained when a run is initialized`` () =
        let champion = CommitOid.create (String('a', 40))
        let alternate = CommitOid.create (String('b', 40))

        let state =
            RunState.createWithHeads
                (RunId.create ())
                Fixtures.config
                champion
                10M
                (Set.ofList [ champion; alternate ])
                DateTimeOffset.UtcNow

        Assert.True(state.ActiveHeads = Set.ofList [ champion; alternate ])

    [<Fact>]
    let ``accepted candidate advances only after frontier confirmation`` () =
        let runId = RunId.create ()
        let parent = CommitOid.create (String('a', 40))
        let candidate = CommitOid.create (String('b', 40))
        let started = DateTimeOffset.UtcNow
        let experimentId = ExperimentId.create ()
        let initial = RunState.create runId Fixtures.config parent 10M started

        let afterStart, _ =
            RunState.transition started (StartRequested(experimentId, started)) initial

        let afterPrepare, _ =
            RunState.transition started (WorktreePrepared experimentId) afterStart

        let afterGeneration, _ =
            RunState.transition
                started
                (GenerationCompleted(experimentId, "thread", Some TokenUsage.zero, Fixtures.summary))
                afterPrepare

        let afterCapture, _ =
            RunState.transition started (CandidateCaptured(experimentId, candidate)) afterGeneration

        let evaluation =
            { SchemaVersion = 1
              Status = EvaluationStatus.Complete
              Constraints = Map [ "build", true; "tests", true ]
              Metrics = Map [ "primary", 11M ]
              Summary = "ok"
              Evidence = [] }

        let pending, effects =
            RunState.transition started (EvaluationCompleted(experimentId, evaluation)) afterCapture

        Assert.Equal(parent, pending.Champion)
        Assert.Contains(effects, fun effect -> effect = PersistAccepted(experimentId, candidate, parent))

        let promoting, _ =
            RunState.transition started (PromotionStarted experimentId) pending

        let accepted, _ =
            RunState.transition started (ChampionAdvanced experimentId) promoting

        Assert.Equal(candidate, accepted.Champion)
        Assert.Equal(11M, accepted.ChampionScore)

    [<Fact>]
    let ``missing terminal usage pauses after the candidate is decided`` () =
        let started = DateTimeOffset.UtcNow

        let initial =
            RunState.create (RunId.create ()) Fixtures.config Fixtures.config.BaseCommit 10M started

        let experimentId = ExperimentId.create ()

        let active, _ =
            RunState.transition started (StartRequested(experimentId, started)) initial

        let generating, _ =
            RunState.transition started (WorktreePrepared experimentId) active

        let withoutUsage, _ =
            RunState.transition started (GenerationCompleted(experimentId, "thread", None, Fixtures.summary)) generating

        Assert.False(withoutUsage.UsageKnown)
        Assert.Equal(PauseAfterCurrent, withoutUsage.Status)

    [<Fact>]
    let ``pause during worktree preparation still advances the current experiment`` () =
        let started = DateTimeOffset.UtcNow
        let experimentId = ExperimentId.create ()

        let initial =
            RunState.create (RunId.create ()) Fixtures.config Fixtures.config.BaseCommit 10M started

        let preparing, _ =
            RunState.transition started (StartRequested(experimentId, started)) initial

        let pausing, _ = RunState.transition started PauseRequested preparing

        let generating, effects =
            RunState.transition started (WorktreePrepared experimentId) pausing

        Assert.Equal(PauseAfterCurrent, generating.Status)
        Assert.Equal(Some Generating, generating.Current |> Option.map _.Phase)
        Assert.Contains(LaunchCodex experimentId, effects)

    [<Fact>]
    let ``stale asynchronous events cannot mutate the current experiment`` () =
        let started = DateTimeOffset.UtcNow
        let currentExperiment = ExperimentId.create ()
        let staleExperiment = ExperimentId.create ()

        let initial =
            RunState.create (RunId.create ()) Fixtures.config Fixtures.config.BaseCommit 10M started

        let preparing, _ =
            RunState.transition started (StartRequested(currentExperiment, started)) initial

        let unchanged, effects =
            RunState.transition started (WorktreePrepared staleExperiment) preparing

        Assert.Equal(preparing, unchanged)
        Assert.Empty effects

    [<Fact>]
    let ``unknown usage cannot be resumed because the token budget is unenforceable`` () =
        let started = DateTimeOffset.UtcNow

        let paused =
            { RunState.create (RunId.create ()) Fixtures.config Fixtures.config.BaseCommit 10M started with
                Status = Paused "Unknown usage."
                UsageKnown = false }

        let unchanged, effects = RunState.transition started ResumeRequested paused
        Assert.Equal(paused, unchanged)
        Assert.Empty effects

    [<Fact>]
    let ``stop before promotion authorization prevents frontier advancement`` () =
        let started = DateTimeOffset.UtcNow
        let experimentId = ExperimentId.create ()
        let parent = Fixtures.config.BaseCommit
        let candidate = CommitOid.create (String('b', 40))
        let initial = RunState.create (RunId.create ()) Fixtures.config parent 10M started

        let preparing, _ =
            RunState.transition started (StartRequested(experimentId, started)) initial

        let generating, _ =
            RunState.transition started (WorktreePrepared experimentId) preparing

        let generated, _ =
            RunState.transition
                started
                (GenerationCompleted(experimentId, "thread", Some TokenUsage.zero, Fixtures.summary))
                generating

        let evaluating, _ =
            RunState.transition started (CandidateCaptured(experimentId, candidate)) generated

        let evaluation =
            { SchemaVersion = 1
              Status = EvaluationStatus.Complete
              Constraints = Map [ "build", true; "tests", true ]
              Metrics = Map [ "primary", 11M ]
              Summary = "ok"
              Evidence = [] }

        let pending, _ =
            RunState.transition started (EvaluationCompleted(experimentId, evaluation)) evaluating

        let stopping, _ = RunState.transition started StopRequested pending

        let notPromoting, promotionEffects =
            RunState.transition started (PromotionStarted experimentId) stopping

        let unchanged, frontierEffects =
            RunState.transition started (ChampionAdvanced experimentId) notPromoting

        Assert.Equal(Stopping, unchanged.Status)
        Assert.Equal(parent, unchanged.Champion)
        Assert.Empty promotionEffects
        Assert.Empty frontierEffects

    [<Fact>]
    let ``accepted target score completes the campaign`` () =
        let started = DateTimeOffset.UtcNow
        let experimentId = ExperimentId.create ()
        let candidate = CommitOid.create (String('b', 40))

        let config =
            { Fixtures.config with
                Metric =
                    { Fixtures.metric with
                        Target = Some 11M } }

        let initial = RunState.create (RunId.create ()) config config.BaseCommit 10M started

        let preparing, _ =
            RunState.transition started (StartRequested(experimentId, started)) initial

        let generating, _ =
            RunState.transition started (WorktreePrepared experimentId) preparing

        let generated, _ =
            RunState.transition
                started
                (GenerationCompleted(experimentId, "thread", Some TokenUsage.zero, Fixtures.summary))
                generating

        let evaluating, _ =
            RunState.transition started (CandidateCaptured(experimentId, candidate)) generated

        let evaluation =
            { SchemaVersion = 1
              Status = EvaluationStatus.Complete
              Constraints = Map [ "build", true; "tests", true ]
              Metrics = Map [ "primary", 11M ]
              Summary = "target"
              Evidence = [] }

        let pending, _ =
            RunState.transition started (EvaluationCompleted(experimentId, evaluation)) evaluating

        let promoting, _ =
            RunState.transition started (PromotionStarted experimentId) pending

        let completed, effects =
            RunState.transition started (ChampionAdvanced experimentId) promoting

        Assert.Equal(Completed "Metric target reached.", completed.Status)
        Assert.Equal(candidate, completed.Champion)
        Assert.DoesNotContain(ScheduleNext, effects)

    [<Fact>]
    let ``start does not exceed a restored experiment budget`` () =
        let started = DateTimeOffset.UtcNow

        let config =
            { Fixtures.config with
                Budgets =
                    { Fixtures.config.Budgets with
                        MaxExperiments = 1 } }

        let restored =
            { RunState.create (RunId.create ()) config config.BaseCommit 10M started with
                Attempted = 1 }

        let completed, effects =
            RunState.transition started (StartRequested(ExperimentId.create (), started)) restored

        Assert.Equal(Completed "Experiment budget exhausted.", completed.Status)
        Assert.True(completed.Current.IsNone)
        Assert.Equal(1, completed.Attempted)
        Assert.DoesNotContain(ScheduleNext, effects)

    [<Fact>]
    let ``duplicate candidate does not consume experiment or stagnation slots`` () =
        let now = DateTimeOffset.UtcNow
        let runId = RunId.create ()
        let experimentId = ExperimentId.create ()
        let matchingId = ExperimentId.create ()

        let initial =
            RunState.create runId Fixtures.config Fixtures.config.BaseCommit 10M now

        let preparing, _ =
            RunState.transition now (StartRequested(experimentId, now)) initial

        let generating, _ =
            RunState.transition now (WorktreePrepared experimentId) preparing

        let usage =
            { TokenUsage.zero with
                InputTokens = 100L
                OutputTokens = 20L }

        let skipped, effects =
            RunState.transition
                now
                (DuplicateCandidateSkipped(experimentId, "ABC123", matchingId, Some usage))
                generating

        Assert.Equal(0, skipped.Attempted)
        Assert.Equal(0, skipped.ConsecutiveNonImprovements)
        Assert.Equal(0, skipped.ConsecutiveFailures)
        Assert.Equal(usage, skipped.Usage)
        Assert.True(skipped.Current.IsNone)
        Assert.Equal(Ready, skipped.Status)
        Assert.Contains(ScheduleNext, effects)

    [<Fact>]
    let ``stagnation allows one synthesis boundary but stops after synthesis`` () =
        let now = DateTimeOffset.UtcNow

        let initial =
            RunState.create (RunId.create ()) Fixtures.config Fixtures.config.BaseCommit 10M now

        let expansionBoundary =
            { initial with
                ConsecutiveNonImprovements = initial.Config.Budgets.MaxConsecutiveNonImprovements
                LastExperimentKind = Some ExperimentKind.Expansion }

        let scheduled, effects =
            RunState.transition now (StartRequested(ExperimentId.create (), now)) expansionBoundary

        Assert.Equal(Running, scheduled.Status)

        Assert.Contains(
            PrepareCandidate(scheduled.Current.Value.Id, scheduled.Current.Value.Parents, initial.Champion),
            effects
        )

        let synthesisBoundary =
            { expansionBoundary with
                LastExperimentKind = Some ExperimentKind.Synthesis }

        let completed, completedEffects =
            RunState.transition now (StartRequested(ExperimentId.create (), now)) synthesisBoundary

        Assert.Equal(Completed "Non-improvement limit reached.", completed.Status)
        Assert.DoesNotContain(ScheduleNext, completedEffects)

    [<Fact>]
    let ``work graph exposes branch children leaves and root lineage`` () =
        let now = DateTimeOffset.UtcNow
        let baseline = CommitOid.create (String('a', 40))
        let first = CommitOid.create (String('b', 40))
        let sibling = CommitOid.create (String('c', 40))
        let grandchild = CommitOid.create (String('d', 40))

        let node sequence parent commit =
            { Id = ExperimentNode(ExperimentId.create ())
              Kind = EvolutionNodeKind.Candidate
              Sequence = sequence
              Parent = Some parent
              Commit = Some commit
              Outcome = EvolutionOutcome.Rejected "fixture"
              Metric = Some(decimal sequence)
              RetainedScore = Some 1M
              Summary = None
              EvaluationSummary = None
              Usage = None
              StartedAt = now
              UpdatedAt = now
              Label = $"Candidate {sequence}" }

        let snapshot =
            { Run =
                { Id = RunId.create ()
                  SourcePath = "/tmp/source"
                  Status = "Completed"
                  CreatedAt = now
                  UpdatedAt = now
                  MetricName = "primary"
                  Direction = Maximize
                  BaselineCommit = Some baseline
                  BaselineScore = Some 1M
                  FrontierScore = Some 1M
                  AttemptCount = 3
                  AcceptedCount = 0 }
              Nodes = [ node 1 baseline first; node 2 baseline sibling; node 3 first grandchild ]
              Frontier = Some baseline
              Edges = []
              Champion = Some baseline
              ActiveHeads = Set.singleton baseline
              Warnings = [] }

        let graph =
            match WorkGraph.ofEvolution snapshot with
            | Ok value -> value
            | Error error -> failwith error.Summary

        Assert.Equal<CommitOid list>([ first; sibling ], WorkGraph.children baseline graph |> List.map _.Commit)
        Assert.Equal<CommitOid list>([ sibling; grandchild ], WorkGraph.leaves graph |> List.map _.Commit)

        let lineage =
            match WorkGraph.lineage grandchild graph with
            | Ok value -> value
            | Error error -> failwith error.Summary

        Assert.Equal<CommitOid list>([ baseline; first; grandchild ], lineage |> List.map _.Commit)

    [<Fact>]
    let ``typed plan schedules only dependency ready work within tool and token budgets`` () =
        let now = DateTimeOffset.UtcNow

        let plan =
            WorkPlan.standardExperiment
                (RunId.create ())
                (ExperimentId.create ())
                "Improve the fixture."
                Fixtures.config.Budgets
                Fixtures.config.Evaluator.Timeout
                now
            |> WorkPlan.validate

        let plan =
            match plan with
            | Ok value -> value
            | Error errors -> failwith (String.concat " " errors)

        let allTools = plan.Items |> Map.values |> Seq.collect _.RequiredTools |> Set.ofSeq

        let capacity =
            { MaxParallelism = 2
              RawTokensRemaining = Fixtures.config.Budgets.MaxRawTokens
              Deadline = now.AddHours 1.0
              AvailableTools = allTools }

        let initial = WorkPlan.initialSchedule plan
        let first = Scheduler.ready now capacity plan initial |> Assert.Single
        Assert.EndsWith("/generate", WorkItemId.value first.Id)

        let generated =
            initial
            |> Scheduler.start now first.Id
            |> Result.bind (Scheduler.succeed now first.Id (Some TokenUsage.zero))

        let generated =
            match generated with
            | Ok value -> value
            | Error error -> failwith error

        let second = Scheduler.ready now capacity plan generated |> Assert.Single
        Assert.EndsWith("/snapshot", WorkItemId.value second.Id)

        let unavailable =
            Scheduler.ready
                now
                { capacity with
                    AvailableTools = Set.empty }
                plan
                initial

        Assert.Empty unavailable

    [<Fact>]
    let ``typed plan validation rejects dependency cycles`` () =
        let now = DateTimeOffset.UtcNow
        let leftId = WorkItemId.create "left"
        let rightId = WorkItemId.create "right"

        let item id dependency =
            { Id = id
              Title = WorkItemId.value id
              Objective = "fixture"
              Dependencies = Set.singleton dependency
              RequiredTools = Set.empty
              Budget =
                { MaxRawTokens = 0L
                  MaxDuration = TimeSpan.FromMinutes 1.0
                  MaxAttempts = 1 }
              Priority = 1 }

        let plan =
            { Id = WorkPlanId.create ()
              RunId = RunId.create ()
              ExperimentId = ExperimentId.create ()
              Objective = "fixture"
              Items = Map [ leftId, item leftId rightId; rightId, item rightId leftId ]
              CreatedAt = now }

        Assert.True(WorkPlan.validate plan |> Result.isError)

    [<Fact>]
    let ``agent aggregation ranks deterministically and reports overlapping paths`` () =
        let parent = Fixtures.config.BaseCommit
        let workItemId = WorkItemId.create "generate"

        let candidate agentId commit metric paths =
            { Assignment =
                { AgentId = AgentId.create agentId
                  Role = AgentRole.Implementer
                  WorkItemId = workItemId
                  Parent = parent
                  Objective = "fixture" }
              Commit = CommitOid.create commit
              Metric = Some metric
              ChangedPaths = Set.ofList paths
              Summary = Fixtures.summary }

        let first = candidate "agent-b" (String('b', 40)) 12M [ "src/shared.fs" ]

        let second =
            candidate "agent-a" (String('c', 40)) 12M [ "src/shared.fs"; "src/a.fs" ]

        let third = candidate "agent-c" (String('d', 40)) 11M [ "src/c.fs" ]
        let aggregation = AgentAggregation.aggregate Maximize [ first; third; second ]

        Assert.Equal(AgentId.create "agent-a", aggregation.Preferred.Value.Assignment.AgentId)

        Assert.Equal<AgentId list>(
            [ AgentId.create "agent-a"; AgentId.create "agent-b" ],
            aggregation.ConflictingPaths["src/shared.fs"]
        )

    [<Fact>]
    let ``knowledge graph requires provenance and retrieves claims through aliases`` () =
        let runId = RunId.create ()

        let entity =
            { Id = KnowledgeEntityId.create ()
              Kind = KnowledgeEntityKind.Symbol
              CanonicalName = "FsHarness.Core.Scheduler.ready"
              Attributes = Map [ "file", "Planning.fs" ] }

        let source =
            { Id = KnowledgeSourceId.create ()
              RunId = runId
              ExperimentId = None
              Kind = KnowledgeSourceKind.Artifact
              Location = "/artifacts/evaluation.json"
              Sha256 = Some(String('a', 64))
              CapturedAt = DateTimeOffset.UtcNow }

        let claim =
            { Id = KnowledgeClaimId.create ()
              RunId = runId
              Subject = entity.Id
              Predicate = "enforces"
              Object = KnowledgeValue.Text "dependency and tool budgets"
              Confidence = 0.95M
              Sources = Set.singleton source.Id
              Supersedes = None
              CreatedAt = DateTimeOffset.UtcNow }

        let graph =
            KnowledgeGraph.empty
            |> KnowledgeGraph.addEntity entity
            |> Result.bind (KnowledgeGraph.addAlias entity.Id "ready scheduler")
            |> Result.bind (KnowledgeGraph.addSource source)
            |> Result.bind (KnowledgeGraph.addClaim claim)

        let graph =
            match graph with
            | Ok value -> value
            | Error error -> failwith error

        let hit = KnowledgeGraph.search "scheduler budgets" 5 graph |> Assert.Single
        Assert.Equal(claim.Id, hit.Claim.Id)
        Assert.Equal(source.Id, hit.Sources.Head.Id)

        let unsupported =
            KnowledgeGraph.addClaim
                { claim with
                    Id = KnowledgeClaimId.create ()
                    Sources = Set.empty }
                graph

        Assert.True(Result.isError unsupported)

module GraphSearchTests =
    let private oid character =
        CommitOid.create (String(character, 40))

    let private getResult =
        function
        | Ok value -> value
        | Error error -> failwith $"Unexpected error: {error}"

    let private parent role commit = { Commit = commit; Role = role }

    let private node commit parents metric family sequence validity status depth =
        { Commit = commit
          Parents = parents
          Metric = metric
          HypothesisFamily = family
          Validity = validity
          ChampionDecision = ChampionDecision.NotPromoted
          SearchStatus = status
          Sequence = sequence
          SynthesisDepth = depth }

    let private graph () =
        let baseline = oid 'a'
        let first = oid 'b'
        let second = oid 'c'
        let child = oid 'd'
        let invalid = oid 'e'

        let baselineNode =
            { node baseline [] (Some 10M) "baseline" 0 EvaluationValidity.Valid SearchStatus.Retained 0 with
                ChampionDecision = ChampionDecision.Promoted }

        { Baseline = baseline
          Champion = first
          Direction = Maximize
          Nodes =
            [ baseline, baselineNode
              first,
              node
                  first
                  [ parent ExperimentParentRole.Primary baseline ]
                  (Some 12M)
                  "allocation"
                  1
                  EvaluationValidity.Valid
                  SearchStatus.ActiveHead
                  0
              second,
              node
                  second
                  [ parent ExperimentParentRole.Primary baseline ]
                  (Some 11M)
                  "kernel"
                  2
                  EvaluationValidity.Valid
                  SearchStatus.Retained
                  0
              child,
              node
                  child
                  [ parent ExperimentParentRole.Primary first ]
                  (Some 11.5M)
                  "allocation"
                  3
                  EvaluationValidity.Valid
                  SearchStatus.Retained
                  0
              invalid,
              node
                  invalid
                  [ parent ExperimentParentRole.Primary baseline ]
                  (Some 99M)
                  "invalid"
                  4
                  (EvaluationValidity.ConstraintFailed [ "tests" ])
                  SearchStatus.Archived
                  0 ]
            |> Map.ofList }

    [<Fact>]
    let ``multi-parent traversal is acyclic and preserves primary lineage`` () =
        let source = graph ()
        let first = oid 'b'
        let second = oid 'c'
        let synthesis = oid 'f'

        let merged =
            node
                synthesis
                [ parent ExperimentParentRole.Primary first
                  parent ExperimentParentRole.Contributor second ]
                (Some 13M)
                "synthesis"
                5
                EvaluationValidity.Valid
                SearchStatus.Retained
                1

        let updated = GraphSearch.addNode merged source |> getResult
        Assert.True(GraphSearch.ancestors synthesis updated |> Set.contains first)
        Assert.True(GraphSearch.ancestors synthesis updated |> Set.contains second)
        Assert.True(GraphSearch.descendants second updated |> Set.contains synthesis)

        let lineage = GraphSearch.primaryLineage synthesis updated |> getResult
        Assert.Equal<CommitOid list>([ oid 'a'; first; synthesis ], lineage |> List.map _.Commit)

    [<Fact>]
    let ``head selection is bounded deterministic and retains valid non-winners`` () =
        let source = graph ()
        let selected = GraphSearch.selectActiveHeads 3 source

        Assert.Equal(3, selected.Length)
        Assert.Equal(oid 'b', selected.Head)
        Assert.Contains(oid 'c', selected)
        Assert.DoesNotContain(oid 'e', selected)
        Assert.Equal<CommitOid list>(selected, GraphSearch.selectActiveHeads 3 source)

    [<Fact>]
    let ``synthesis pairs exclude ancestors and prior attempts`` () =
        let source = graph ()
        let first = oid 'b'
        let second = oid 'c'
        let child = oid 'd'
        let eligible = GraphSearch.synthesisPairs 2 Set.empty [ first; second ] source

        Assert.Single eligible |> ignore
        Assert.Equal(first, eligible.Head.Primary)
        Assert.Equal(second, eligible.Head.Contributor)
        Assert.Empty(GraphSearch.synthesisPairs 2 Set.empty [ first; child ] source)

        let attempted = Set.singleton (first, second)
        Assert.Empty(GraphSearch.synthesisPairs 2 attempted [ first; second ] source)
        Assert.Empty(GraphSearch.synthesisPairs 2 attempted [ second; first ] source)

    [<Fact>]
    let ``synthesis cadence and reservation remain bounded`` () =
        Assert.False(GraphSearch.shouldSynthesize Defaults.graphSearch 2 2)
        Assert.True(GraphSearch.shouldSynthesize Defaults.graphSearch 3 0)
        Assert.True(GraphSearch.shouldSynthesize Defaults.graphSearch 0 3)
        Assert.Equal(2, GraphSearch.reservedSynthesisSlots Defaults.graphSearch 10)

module RepositoryKnowledgeGraphTests =
    let private getResult =
        function
        | Ok value -> value
        | Error error -> failwith $"Unexpected error: {error}"

    let private node run kind id name attributes =
        { Id = GraphNodeId.create id
          Kind = kind
          CanonicalName = name
          Attributes = attributes
          Version = 1
          OriginRunId = run
          CreatedAt = DateTimeOffset.UtcNow }

    [<Fact>]
    let ``repository updates are idempotent and bounded queries keep stable citations`` () =
        let runId = RunId.create ()
        let sourceId = KnowledgeSourceId.create ()
        let commit = node runId GraphNodeKind.Commit "commit:a" "a" Map.empty
        let claim = node runId GraphNodeKind.Claim "claim:one" "faster kernel" Map.empty

        let evaluation =
            node runId GraphNodeKind.Evaluation "evaluation:one" "evaluation" (Map [ "rubric", "build+metric" ])

        let edge
            (id: string)
            (fromNode: RepositoryGraphNode)
            relation
            (toNode: RepositoryGraphNode)
            : RepositoryGraphEdge =
            { Id = GraphEdgeId.create id
              From = fromNode.Id
              Relation = relation
              To = toNode.Id
              Confidence = 1M
              Provenance = GraphProvenance.Sourced(Set.singleton sourceId)
              OriginRunId = runId
              ValidFrom = DateTimeOffset.UtcNow
              ValidTo = None }

        let update =
            { ProjectId = "repo"
              RunId = runId
              AgentId = "test"
              IdempotencyKey = "update:one"
              Nodes = [ commit; claim; evaluation ]
              Edges =
                [ edge "edge:claim" claim GraphRelationKind.Supports commit
                  edge "edge:evaluation" evaluation GraphRelationKind.Evaluates commit ] }

        let once =
            RepositoryKnowledgeGraph.empty "repo"
            |> RepositoryKnowledgeGraph.apply update
            |> getResult

        let twice = once |> RepositoryKnowledgeGraph.apply update |> getResult
        Assert.Equal(2, twice.Edges.Count)

        let context =
            RepositoryKnowledgeGraph.query
                { Seeds = Set.singleton claim.Id
                  MaxHops = 2
                  MaxEdges = 10
                  MaxCharacters = 6_000
                  AllowedRelations = Set [ GraphRelationKind.Supports; GraphRelationKind.Evaluates ]
                  AsOf = None
                  IncludeConflicts = true }
                twice

        Assert.Contains("[edge:claim]", context.Serialized)
        Assert.Contains("[edge:evaluation]", context.Serialized)
        Assert.False context.Truncated

    [<Fact>]
    let ``shared repository nodes retain identical versions and append changed content`` () =
        let firstRun = RunId.create ()
        let secondRun = RunId.create ()
        let nodeId = GraphNodeId.create "commit:shared"
        let createdAt = DateTimeOffset.UtcNow

        let original =
            RepositoryKnowledgeGraph.versionNode
                firstRun
                createdAt
                GraphNodeKind.Commit
                nodeId
                "shared"
                Map.empty
                (RepositoryKnowledgeGraph.empty "repo")

        let graph =
            { RepositoryKnowledgeGraph.empty "repo" with
                Nodes = Map [ nodeId, [ original ] ] }

        let retained =
            RepositoryKnowledgeGraph.versionNode
                secondRun
                (createdAt.AddMinutes 1.0)
                GraphNodeKind.Commit
                nodeId
                "shared"
                Map.empty
                graph

        let changed =
            RepositoryKnowledgeGraph.versionNode
                secondRun
                (createdAt.AddMinutes 2.0)
                GraphNodeKind.Commit
                nodeId
                "shared"
                (Map [ "verified", "true" ])
                graph

        Assert.Equal(original, retained)
        Assert.Equal(2, changed.Version)
        Assert.Equal(secondRun, changed.OriginRunId)
        Assert.Equal("true", changed.Attributes["verified"])

    [<Fact>]
    let ``artifact provenance invariants and exact aliases are enforced`` () =
        let runId = RunId.create ()

        let invalidArtifact =
            node runId GraphNodeKind.Artifact "artifact:bad" "bad" Map.empty

        let invalidUpdate =
            { ProjectId = "repo"
              RunId = runId
              AgentId = "test"
              IdempotencyKey = "bad"
              Nodes = [ invalidArtifact ]
              Edges = [] }

        Assert.True(
            RepositoryKnowledgeGraph.empty "repo"
            |> RepositoryKnowledgeGraph.apply invalidUpdate
            |> Result.isError
        )

        let orphanClaim =
            node runId GraphNodeKind.Claim "claim:orphan" "unsupported" Map.empty

        Assert.True(
            RepositoryKnowledgeGraph.empty "repo"
            |> RepositoryKnowledgeGraph.apply
                { invalidUpdate with
                    IdempotencyKey = "orphan"
                    Nodes = [ orphanClaim ] }
            |> Result.isError
        )

        let entity =
            node
                runId
                GraphNodeKind.Entity
                "entity:planner"
                "Graph Scheduler"
                (Map [ "aliases", "scheduler|beam planner" ])

        let graph =
            RepositoryKnowledgeGraph.empty "repo"
            |> RepositoryKnowledgeGraph.apply
                { invalidUpdate with
                    IdempotencyKey = "entity"
                    Nodes = [ entity ] }
            |> getResult

        Assert.Single(RepositoryKnowledgeGraph.resolveExact GraphNodeKind.Entity "Beam Planner" graph)
        |> ignore

module BenchmarkTests =
    let private episodes rawSol rawRatchet =
        [ for task in HypothesisBenchmark.preset.Tasks do
              for arm in HypothesisBenchmark.preset.Arms do
                  let tokens =
                      if arm.Id = HypothesisBenchmark.SolArmId then
                          rawSol
                      else
                          rawRatchet

                  { TaskId = task.Id
                    ArmId = arm.Id
                    QualityPassed = true
                    CriticalQualityOrSafetyDefect = false
                    RawTokens = Some tokens } ]

    [<Fact>]
    let ``frozen preset is twelve episodes and discloses aggregate cap`` () =
        Assert.Equal(4, HypothesisBenchmark.preset.Tasks.Length)
        Assert.Equal(3, HypothesisBenchmark.preset.Arms.Length)
        Assert.Equal(600_000L, HypothesisBenchmark.preset.DisclosedAggregateTokenBudget)

    [<Fact>]
    let ``ratchet is promising only at or below eighty percent`` () =
        match HypothesisBenchmark.assess (episodes 50_000L 40_000L) with
        | Promising comparison -> Assert.Equal(160_000L, comparison.RatchetBudgetedTokensToQuality)
        | result -> Assert.Fail $"Unexpected assessment: {result}"

    [<Fact>]
    let ``any missing usage makes the smoke test inconclusive`` () =
        let values = episodes 50_000L 40_000L
        let missing = { values.Head with RawTokens = None }

        match HypothesisBenchmark.assess (missing :: values.Tail) with
        | Inconclusive _ -> ()
        | result -> Assert.Fail $"Unexpected assessment: {result}"
