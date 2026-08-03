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
          MaxInconclusiveRetries = 2 }

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
          Budgets = Defaults.budgets
          PromotionMode = AutoWhenStrictlyBetter }

    let summary =
        { Hypothesis = "One change"
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
                  FrontierScore = 1M
                  Metric = Fixtures.metric
                  Profile = Defaults.promptProfile
                  PreviousEvaluation = None
                  Memories = memories }

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
                  FrontierScore = 1M
                  Metric = Fixtures.metric
                  Profile = profile
                  PreviousEvaluation = Some evaluation
                  Memories = memories }

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
    let ``accepted candidate advances only after frontier confirmation`` () =
        let runId = RunId.create ()
        let parent = CommitOid.create (String('a', 40))
        let candidate = CommitOid.create (String('b', 40))
        let started = DateTimeOffset.UtcNow
        let experimentId = ExperimentId.create ()
        let initial = RunState.create runId Fixtures.config parent 10M started

        let afterStart, _ =
            RunState.transition started (StartRequested(experimentId, started)) initial

        let afterPrepare, _ = RunState.transition started WorktreePrepared afterStart

        let afterGeneration, _ =
            RunState.transition
                started
                (GenerationCompleted("thread", Some TokenUsage.zero, Fixtures.summary))
                afterPrepare

        let afterCapture, _ =
            RunState.transition started (CandidateCaptured candidate) afterGeneration

        let evaluation =
            { SchemaVersion = 1
              Status = EvaluationStatus.Complete
              Constraints = Map [ "build", true; "tests", true ]
              Metrics = Map [ "primary", 11M ]
              Summary = "ok"
              Evidence = [] }

        let pending, effects =
            RunState.transition started (EvaluationCompleted evaluation) afterCapture

        Assert.Equal(parent, pending.Frontier)
        Assert.Contains(effects, fun effect -> effect = PersistAccepted(experimentId, candidate, parent))

        let accepted, _ = RunState.transition started FrontierAdvanced pending
        Assert.Equal(candidate, accepted.Frontier)
        Assert.Equal(11M, accepted.FrontierScore)

    [<Fact>]
    let ``missing terminal usage pauses after the candidate is decided`` () =
        let started = DateTimeOffset.UtcNow

        let initial =
            RunState.create (RunId.create ()) Fixtures.config Fixtures.config.BaseCommit 10M started

        let active, _ =
            RunState.transition started (StartRequested(ExperimentId.create (), started)) initial

        let generating, _ = RunState.transition started WorktreePrepared active

        let withoutUsage, _ =
            RunState.transition started (GenerationCompleted("thread", None, Fixtures.summary)) generating

        Assert.False(withoutUsage.UsageKnown)
        Assert.Equal(PauseAfterCurrent, withoutUsage.Status)

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
