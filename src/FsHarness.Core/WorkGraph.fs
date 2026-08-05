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
      Champion: CommitOid
      ActiveHeads: Set<CommitOid>
      Edges: EvolutionEdge list
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

            let champion = snapshot.Champion |> Option.defaultValue frontier

            let edges =
                if List.isEmpty snapshot.Edges then
                    nodes
                    |> Map.values
                    |> Seq.choose (fun node ->
                        node.Parent
                        |> Option.map (fun parent ->
                            { Id = $"legacy:{CommitOid.value parent}:{CommitOid.value node.Commit}"
                              Parent = parent
                              Child = node.Commit
                              Kind = ExperimentKind.Expansion
                              Role = ExperimentParentRole.Primary }))
                    |> List.ofSeq
                else
                    snapshot.Edges

            if not (Map.containsKey champion nodes) then
                Error(
                    graphError "work_graph.champion_missing" "The champion is not present in the persisted work graph."
                )
            else
                let missingParents =
                    edges
                    |> Seq.map _.Parent
                    |> Seq.filter (fun parent -> not (Map.containsKey parent nodes))
                    |> Seq.distinct
                    |> Seq.toList

                let missingChildren =
                    edges
                    |> Seq.map _.Child
                    |> Seq.filter (fun child -> not (Map.containsKey child nodes))
                    |> Seq.distinct
                    |> Seq.toList

                if List.isEmpty missingParents && List.isEmpty missingChildren then
                    Ok
                        { Baseline = baseline
                          Frontier = frontier
                          Champion = champion
                          ActiveHeads = snapshot.ActiveHeads
                          Edges = edges
                          Nodes = nodes }
                else
                    Error(
                        graphError "work_graph.parent_missing" "One or more work-graph edges refer to a missing commit."
                        |> HarnessError.withDetail (
                            (missingParents @ missingChildren)
                            |> List.map CommitOid.value
                            |> String.concat ", "
                        )
                    )
        | _ ->
            Error(graphError "work_graph.root_missing" "The persisted work graph has no verified baseline or frontier.")

    let tryFind commit graph = Map.tryFind commit graph.Nodes

    let children parent graph =
        graph.Edges
        |> Seq.filter (fun edge -> edge.Parent = parent)
        |> Seq.choose (fun edge -> Map.tryFind edge.Child graph.Nodes)
        |> Seq.distinctBy _.Commit
        |> Seq.sortBy _.Sequence
        |> List.ofSeq

    let parents child graph =
        graph.Edges
        |> List.filter (fun edge -> edge.Child = child)
        |> List.sortBy (fun edge ->
            match edge.Role with
            | ExperimentParentRole.Primary -> 0
            | ExperimentParentRole.Contributor -> 1)

    let activeHeads graph =
        graph.ActiveHeads
        |> Set.toList
        |> List.choose (fun commit -> Map.tryFind commit graph.Nodes)

    let leaves graph =
        let parents = graph.Edges |> Seq.map _.Parent |> Set.ofSeq

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
                    match
                        parents current graph
                        |> List.tryFind (fun edge -> edge.Role = ExperimentParentRole.Primary)
                    with
                    | None -> Ok(node :: collected)
                    | Some edge -> walk (Set.add current visited) edge.Parent (node :: collected)

        walk Set.empty commit []

    let ancestors commit graph =
        let rec walk pending visited =
            match pending with
            | [] -> visited
            | current :: remaining when Set.contains current visited -> walk remaining visited
            | current :: remaining ->
                let next = parents current graph |> List.map _.Parent
                walk (next @ remaining) (Set.add current visited)

        parents commit graph |> List.map _.Parent |> (fun roots -> walk roots Set.empty)

    let descendants commit graph =
        let rec walk pending visited =
            match pending with
            | [] -> visited
            | current :: remaining when Set.contains current visited -> walk remaining visited
            | current :: remaining ->
                let next = children current graph |> List.map _.Commit
                walk (next @ remaining) (Set.add current visited)

        children commit graph
        |> List.map _.Commit
        |> fun roots -> walk roots Set.empty

    let contains commit graph = Map.containsKey commit graph.Nodes
