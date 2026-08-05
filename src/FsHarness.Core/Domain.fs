namespace FsHarness.Core

open System

[<Struct>]
type RunId = private RunId of Guid

[<RequireQualifiedAccess>]
module RunId =
    let create () = RunId(Guid.NewGuid())
    let ofGuid value = RunId value
    let value (RunId value) = value
    let text (RunId value) = string value

[<Struct>]
type ExperimentId = private ExperimentId of Guid

[<RequireQualifiedAccess>]
module ExperimentId =
    let create () = ExperimentId(Guid.NewGuid())
    let ofGuid value = ExperimentId value
    let value (ExperimentId value) = value
    let text (ExperimentId value) = string value

[<Struct>]
type CommitOid = private CommitOid of string

[<RequireQualifiedAccess>]
module CommitOid =
    let create value = CommitOid value
    let value (CommitOid value) = value

type MetricDirection =
    | Minimize
    | Maximize

type ReasoningEffort =
    | Low
    | Medium
    | High
    | XHigh
    | Max

[<RequireQualifiedAccess>]
module ReasoningEffort =
    let toConfigValue effort =
        match effort with
        | Low -> "low"
        | Medium -> "medium"
        | High -> "high"
        | XHigh -> "xhigh"
        | Max -> "max"

type PromotionMode =
    | AutoWhenStrictlyBetter
    | ReviewStrictWinners

[<RequireQualifiedAccess>]
type ExperimentKind =
    | Expansion
    | Synthesis

[<RequireQualifiedAccess>]
type ExperimentParentRole =
    | Primary
    | Contributor

type ExperimentParent =
    { Commit: CommitOid
      Role: ExperimentParentRole }

[<RequireQualifiedAccess>]
type EvaluationValidity =
    | Pending
    | Valid
    | ConstraintFailed of string list
    | Inconclusive of string
    | InfrastructureFailed of string

[<RequireQualifiedAccess>]
type ChampionDecision =
    | Pending
    | Promoted
    | NotPromoted
    | RejectedByUser

[<RequireQualifiedAccess>]
type SearchStatus =
    | ActiveHead
    | Retained
    | Exhausted
    | ConflictBlocked
    | Archived

type TokenUsage =
    { InputTokens: int64
      CachedInputTokens: int64
      OutputTokens: int64
      ReasoningOutputTokens: int64 }

[<RequireQualifiedAccess>]
module TokenUsage =
    let zero =
        { InputTokens = 0L
          CachedInputTokens = 0L
          OutputTokens = 0L
          ReasoningOutputTokens = 0L }

    let normalize usage =
        { InputTokens = max 0L usage.InputTokens
          CachedInputTokens = usage.CachedInputTokens |> max 0L |> min (max 0L usage.InputTokens)
          OutputTokens = max 0L usage.OutputTokens
          ReasoningOutputTokens = usage.ReasoningOutputTokens |> max 0L |> min (max 0L usage.OutputTokens) }

    let add left right =
        { InputTokens = left.InputTokens + right.InputTokens
          CachedInputTokens = left.CachedInputTokens + right.CachedInputTokens
          OutputTokens = left.OutputTokens + right.OutputTokens
          ReasoningOutputTokens = left.ReasoningOutputTokens + right.ReasoningOutputTokens }

    let rawTotal usage = usage.InputTokens + usage.OutputTokens

    let uncachedTotal usage =
        max 0L (usage.InputTokens - usage.CachedInputTokens) + usage.OutputTokens

    let visibleOutput usage =
        max 0L (usage.OutputTokens - usage.ReasoningOutputTokens)

type ExperimentSummary =
    { HypothesisFamily: string
      Hypothesis: string
      ChangeSummary: string
      ExpectedEffect: string
      ValidationNotes: string list
      ReusableLesson: string }

type MemorySummary =
    { ExperimentId: ExperimentId
      Outcome: string
      Metric: decimal option
      Summary: ExperimentSummary }

type EvolutionNodeId =
    | BaselineNode
    | ExperimentNode of ExperimentId

[<RequireQualifiedAccess>]
type EvolutionNodeKind =
    | Baseline
    | Seed
    | Candidate

[<RequireQualifiedAccess>]
type EvolutionOutcome =
    | Active of string
    | Accepted
    | Rejected of string
    | Failed of string
    | Inconclusive of string
    | Cancelled
    | Unknown of string

type EvolutionRunSummary =
    { Id: RunId
      SourcePath: string
      Status: string
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset
      MetricName: string
      Direction: MetricDirection
      BaselineCommit: CommitOid option
      BaselineScore: decimal option
      FrontierScore: decimal option
      AttemptCount: int
      AcceptedCount: int }

type EvolutionNode =
    { Id: EvolutionNodeId
      Kind: EvolutionNodeKind
      Sequence: int
      Parent: CommitOid option
      Commit: CommitOid option
      Outcome: EvolutionOutcome
      Metric: decimal option
      RetainedScore: decimal option
      Summary: ExperimentSummary option
      EvaluationSummary: string option
      Usage: TokenUsage option
      StartedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset
      Label: string }

type EvolutionEdge =
    { Id: string
      Parent: CommitOid
      Child: CommitOid
      Kind: ExperimentKind
      Role: ExperimentParentRole }

type EvolutionSnapshot =
    { Run: EvolutionRunSummary
      Nodes: EvolutionNode list
      Frontier: CommitOid option
      Edges: EvolutionEdge list
      Champion: CommitOid option
      ActiveHeads: Set<CommitOid>
      Warnings: string list }

type HarnessErrorCategory =
    | Configuration
    | Codex
    | Git
    | Evaluation
    | Persistence
    | Permission
    | Protocol
    | Budget
    | Recovery

type HarnessError =
    { Code: string
      Category: HarnessErrorCategory
      Summary: string
      Detail: string option
      Retryable: bool
      SuggestedActions: string list
      ExperimentId: ExperimentId option
      LogPath: string option }

[<RequireQualifiedAccess>]
module HarnessError =
    let create code category summary =
        { Code = code
          Category = category
          Summary = summary
          Detail = None
          Retryable = false
          SuggestedActions = []
          ExperimentId = None
          LogPath = None }

    let withDetail detail error = { error with Detail = Some detail }
    let retryable error = { error with Retryable = true }
