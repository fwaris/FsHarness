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
    { Hypothesis: string
      ChangeSummary: string
      ExpectedEffect: string
      ValidationNotes: string list
      ReusableLesson: string }

type MemorySummary =
    { ExperimentId: ExperimentId
      Outcome: string
      Metric: decimal option
      Summary: ExperimentSummary }

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
