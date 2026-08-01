namespace FsHarness.Core

type EvaluationResult =
    { SchemaVersion: int
      Constraints: Map<string, bool>
      Metrics: Map<string, decimal>
      Summary: string
      Evidence: string list }

type RejectionReason =
    | ConstraintFailed of string list
    | MetricMissing of string
    | NotStrictlyBetter of frontier: decimal * candidate: decimal * minDelta: decimal

type CandidateDecision =
    | StrictImprovement of candidateScore: decimal
    | Rejected of RejectionReason

[<RequireQualifiedAccess>]
module Evaluation =
    let private improves metric frontier candidate =
        match metric.Direction with
        | Maximize -> candidate >= frontier + metric.MinDelta && candidate > frontier
        | Minimize -> candidate <= frontier - metric.MinDelta && candidate < frontier

    let decide metric requiredConstraints frontier result =
        let failedConstraints =
            requiredConstraints
            |> List.filter (fun name -> result.Constraints |> Map.tryFind name <> Some true)

        match failedConstraints with
        | _ :: _ -> Rejected(ConstraintFailed failedConstraints)
        | [] ->
            match result.Metrics |> Map.tryFind metric.Name with
            | None -> Rejected(MetricMissing metric.Name)
            | Some candidate when improves metric frontier candidate -> StrictImprovement candidate
            | Some candidate -> Rejected(NotStrictlyBetter(frontier, candidate, metric.MinDelta))

    let targetReached metric score =
        match metric.Target with
        | None -> false
        | Some target ->
            match metric.Direction with
            | Maximize -> score >= target
            | Minimize -> score <= target
