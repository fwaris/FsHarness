namespace FsHarness.Core

type WorkGraphNode =
    { ExperimentId: ExperimentId option
      Commit: CommitOid
      Parent: CommitOid option
      Outcome: EvolutionOutcome
      Metric: decimal option
      Sequence: int
      Label: string }

type WorkGraph =
    { Baseline: CommitOid
      Frontier: CommitOid
      Nodes: Map<CommitOid, WorkGraphNode> }

[<RequireQualifiedAccess>]
module WorkGraph =
    let private graphError code summary =
        HarnessError.create code HarnessErrorCategory.Recovery summary

    let ofEvolution (snapshot: EvolutionSnapshot) =
        match snapshot.Run.BaselineCommit, snapshot.Frontier with
        | Some baseline, Some frontier ->
            let baselineNode =
                { ExperimentId = None
                  Commit = baseline
                  Parent = None
                  Outcome = EvolutionOutcome.Unknown "Baseline"
                  Metric = snapshot.Run.BaselineScore
                  Sequence = 0
                  Label = "Baseline" }

            let nodes =
                snapshot.Nodes
                |> List.choose (fun node ->
                    node.Commit
                    |> Option.map (fun commit ->
                        commit,
                        { ExperimentId =
                            match node.Id with
                            | ExperimentNode experimentId -> Some experimentId
                            | BaselineNode -> None
                          Commit = commit
                          Parent = node.Parent
                          Outcome = node.Outcome
                          Metric = node.Metric
                          Sequence = node.Sequence
                          Label = node.Label }))
                |> Map.ofList
                |> Map.add baseline baselineNode

            if not (Map.containsKey frontier nodes) then
                Error(
                    graphError
                        "work_graph.frontier_missing"
                        "The retained frontier is not present in the persisted work graph."
                )
            else
                let missingParents =
                    nodes
                    |> Map.values
                    |> Seq.choose _.Parent
                    |> Seq.filter (fun parent -> not (Map.containsKey parent nodes))
                    |> Seq.distinct
                    |> Seq.toList

                if List.isEmpty missingParents then
                    Ok
                        { Baseline = baseline
                          Frontier = frontier
                          Nodes = nodes }
                else
                    Error(
                        graphError
                            "work_graph.parent_missing"
                            "One or more work-graph nodes refer to a missing parent commit."
                        |> HarnessError.withDetail (missingParents |> List.map CommitOid.value |> String.concat ", ")
                    )
        | _ ->
            Error(graphError "work_graph.root_missing" "The persisted work graph has no verified baseline or frontier.")

    let tryFind commit graph = Map.tryFind commit graph.Nodes

    let children parent graph =
        graph.Nodes
        |> Map.values
        |> Seq.filter (fun node -> node.Parent = Some parent)
        |> Seq.sortBy _.Sequence
        |> List.ofSeq

    let leaves graph =
        let parents = graph.Nodes |> Map.values |> Seq.choose _.Parent |> Set.ofSeq

        graph.Nodes
        |> Map.values
        |> Seq.filter (fun node -> not (Set.contains node.Commit parents))
        |> Seq.sortBy _.Sequence
        |> List.ofSeq

    let lineage commit graph =
        let rec walk visited current collected =
            if Set.contains current visited then
                Error(graphError "work_graph.cycle" "A cycle was detected while traversing persisted work lineage.")
            else
                match Map.tryFind current graph.Nodes with
                | None ->
                    Error(
                        graphError "work_graph.commit_missing" "The requested commit is not present in the work graph."
                    )
                | Some node ->
                    match node.Parent with
                    | None -> Ok(node :: collected)
                    | Some parent -> walk (Set.add current visited) parent (node :: collected)

        walk Set.empty commit []

    let contains commit graph = Map.containsKey commit graph.Nodes
