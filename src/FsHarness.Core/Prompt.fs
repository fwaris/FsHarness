namespace FsHarness.Core

open System
open System.Text

type PromptContext =
    { Objective: string
      EditablePaths: string list
      FrontierScore: decimal
      Metric: MetricSpec
      PreviousEvaluation: EvaluationResult option
      Memories: MemorySummary list }

[<RequireQualifiedAccess>]
module Prompt =
    [<Literal>]
    let MaxMemoryCount = 5

    [<Literal>]
    let MaxMemoryCharacters = 6_000

    let private truncate length (text: string) =
        if String.IsNullOrEmpty text || text.Length <= length then
            text
        else
            text.Substring(0, max 0 (length - 1)) + "…"

    let private renderMemory (memory: MemorySummary) =
        let metric = memory.Metric |> Option.map string |> Option.defaultValue "unknown"

        $"Outcome: {memory.Outcome}; metric: {metric}\nHypothesis: {memory.Summary.Hypothesis}\nChange: {memory.Summary.ChangeSummary}\nLesson: {memory.Summary.ReusableLesson}"

    let private renderEvaluation (evaluation: EvaluationResult) =
        let constraints =
            evaluation.Constraints
            |> Map.toList
            |> List.map (fun (name, passed) -> $"{name}={passed}")
            |> String.concat ", "

        let metrics =
            evaluation.Metrics
            |> Map.toList
            |> List.map (fun (name, value) -> $"{name}={value}")
            |> String.concat ", "

        let evidence = String.concat "; " evaluation.Evidence
        $"Constraints: {constraints}\nMetrics: {metrics}\nSummary: {evaluation.Summary}\nEvidence: {evidence}"

    let private boundedMemories (memories: MemorySummary list) =
        let selected = memories |> List.truncate MaxMemoryCount
        let mutable remaining = MaxMemoryCharacters

        selected
        |> List.choose (fun memory ->
            if remaining <= 0 then
                None
            else
                let rendered = renderMemory memory |> truncate remaining
                remaining <- remaining - rendered.Length
                Some rendered)

    let build context =
        let direction =
            match context.Metric.Direction with
            | Maximize -> "maximize"
            | Minimize -> "minimize"

        let builder = StringBuilder()

        builder.AppendLine("You are one worker in a measured, reversible engineering ratchet.")
        |> ignore

        builder.AppendLine(
            "Inspect the current retained repository state, choose exactly one motivated change, implement it, and validate it locally."
        )
        |> ignore

        builder.AppendLine("Do not broaden scope, modify protected files, use network access, or spawn other agents.")
        |> ignore

        builder.AppendLine() |> ignore
        builder.AppendLine("Objective:") |> ignore
        builder.AppendLine(context.Objective) |> ignore
        builder.AppendLine() |> ignore

        builder.AppendLine(
            $"Primary metric: {context.Metric.Name}; direction: {direction}; retained score: {context.FrontierScore}; required minimum delta: {context.Metric.MinDelta}."
        )
        |> ignore

        let editablePaths = String.concat ", " context.EditablePaths
        builder.AppendLine($"Editable paths: {editablePaths}") |> ignore

        context.PreviousEvaluation
        |> Option.iter (fun evaluation ->
            builder.AppendLine() |> ignore
            builder.AppendLine("Most recent evaluator feedback:") |> ignore
            builder.AppendLine(evaluation |> renderEvaluation |> truncate 2_000) |> ignore)

        let memories = boundedMemories context.Memories

        if not (List.isEmpty memories) then
            builder.AppendLine() |> ignore

            builder.AppendLine("Bounded experiment memory (distilled observations, not transcripts):")
            |> ignore

            memories
            |> List.iteri (fun index memory -> builder.AppendLine($"[{index + 1}] {memory}") |> ignore)

        builder.AppendLine() |> ignore

        builder.AppendLine(
            "Your final response must conform to the supplied JSON schema and report only observable summaries, not hidden chain-of-thought."
        )
        |> ignore

        builder.ToString()
