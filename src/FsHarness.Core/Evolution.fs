namespace FsHarness.Core

open System

type EvolutionScorePoint =
    { NodeId: EvolutionNodeId
      Metric: decimal option
      RetainedScore: decimal option
      Sequence: int }

[<RequireQualifiedAccess>]
module Evolution =
    let isAccepted (node: EvolutionNode) =
        match node.Outcome with
        | EvolutionOutcome.Accepted -> true
        | _ -> false

    let outcomeText outcome =
        match outcome with
        | EvolutionOutcome.Active phase -> phase
        | EvolutionOutcome.Accepted -> "Accepted"
        | EvolutionOutcome.Rejected reason ->
            if String.IsNullOrWhiteSpace reason then
                "Rejected"
            else
                $"Rejected: {reason}"
        | EvolutionOutcome.Failed reason ->
            if String.IsNullOrWhiteSpace reason then
                "Failed"
            else
                $"Failed: {reason}"
        | EvolutionOutcome.Inconclusive reason ->
            if String.IsNullOrWhiteSpace reason then
                "Inconclusive"
            else
                $"Inconclusive: {reason}"
        | EvolutionOutcome.Cancelled -> "Cancelled"
        | EvolutionOutcome.Unknown reason ->
            if String.IsNullOrWhiteSpace reason then
                "Unknown"
            else
                $"Unknown: {reason}"

    let private accepts direction current candidate =
        match candidate with
        | None -> false
        | Some value ->
            match direction, current with
            | _, None -> true
            | Maximize, Some retained -> value > retained
            | Minimize, Some retained -> value < retained

    let scorePoints direction baseline (nodes: EvolutionNode list) =
        let initial: EvolutionScorePoint =
            { NodeId = BaselineNode
              Metric = baseline
              RetainedScore = baseline
              Sequence = 0 }

        let _, points =
            nodes
            |> List.sortBy _.Sequence
            |> List.fold
                (fun (retained, points) (node: EvolutionNode) ->
                    let nextRetained =
                        if isAccepted node && accepts direction retained node.Metric then
                            node.Metric
                        else
                            retained

                    nextRetained,
                    { NodeId = node.Id
                      Metric = node.Metric
                      RetainedScore = nextRetained
                      Sequence = node.Sequence }
                    :: points)
                (baseline, [ initial ])

        List.rev points

    let acceptedCount nodes =
        nodes |> List.filter isAccepted |> List.length
