namespace FsHarness.App

open FsHarness.Core

type EvolutionPoint = { X: float; Y: float }

type EvolutionNodeLayout =
    { Node: EvolutionNode
      X: float
      Y: float
      Width: float
      Height: float
      IsChampion: bool
      IsActiveHead: bool
      IsSynthesis: bool }

type EvolutionEdgeLayout =
    { From: EvolutionPoint
      To: EvolutionPoint
      Kind: ExperimentKind
      Role: ExperimentParentRole }

type EvolutionScoreLayout =
    { NodeId: EvolutionNodeId
      X: float
      MetricY: float option
      RetainedY: float option
      Metric: decimal option
      RetainedScore: decimal option }

type EvolutionLayout =
    { Width: float
      GraphHeight: float
      ChartHeight: float
      Nodes: EvolutionNodeLayout list
      Edges: EvolutionEdgeLayout list
      Scores: EvolutionScoreLayout list
      AxisMinimum: decimal option
      AxisMaximum: decimal option }

[<RequireQualifiedAccess>]
module EvolutionLayout =
    let private nodeWidth = 112.0
    let private nodeHeight = 52.0
    let private step = 148.0
    let private rowStep = 82.0

    let private scoreY minimum maximum height value =
        let range = maximum - minimum
        let fraction = float ((maximum - value) / range)
        28.0 + fraction * (height - 56.0)

    let buildForSelection (snapshot: EvolutionSnapshot) (selected: EvolutionNodeId option) : EvolutionLayout =
        let allNodes =
            { Id = BaselineNode
              Kind = EvolutionNodeKind.Baseline
              Sequence = 0
              Parent = None
              Commit = snapshot.Run.BaselineCommit
              Outcome = EvolutionOutcome.Unknown "Baseline"
              Metric = snapshot.Run.BaselineScore
              RetainedScore = snapshot.Run.BaselineScore
              Summary = None
              EvaluationSummary = None
              Usage = None
              StartedAt = snapshot.Run.CreatedAt
              UpdatedAt = snapshot.Run.UpdatedAt
              Label = "Baseline" }
            :: snapshot.Nodes

        let ordered = allNodes |> List.sortBy (fun node -> node.Sequence, node.Label)

        let nodeByCommit =
            ordered
            |> List.choose (fun node -> node.Commit |> Option.map (fun commit -> commit, node))
            |> Map.ofList

        let authoritativeEdges =
            let storedChildren = snapshot.Edges |> List.map _.Child |> Set.ofList

            let compatibilityEdges =
                snapshot.Nodes
                |> List.choose (fun node ->
                    match node.Parent, node.Commit with
                    | Some parent, Some child when not (Set.contains child storedChildren) ->
                        Some
                            { Id = $"compat:{CommitOid.value parent}:{CommitOid.value child}"
                              Parent = parent
                              Child = child
                              Kind = ExperimentKind.Expansion
                              Role = ExperimentParentRole.Primary }
                    | _ -> None)

            snapshot.Edges @ compatibilityEdges

        let parentsByChild = authoritativeEdges |> List.groupBy _.Child |> Map.ofList

        let baselineCommit = snapshot.Run.BaselineCommit

        let rec commitDepth visited commit =
            if baselineCommit = Some commit then
                0
            elif Set.contains commit visited then
                0
            else
                match Map.tryFind commit parentsByChild with
                | None
                | Some [] -> 1
                | Some edges ->
                    1
                    + (edges
                       |> List.map (fun edge -> commitDepth (Set.add commit visited) edge.Parent)
                       |> List.max)

        let depthFor (node: EvolutionNode) =
            match node.Kind, node.Commit with
            | EvolutionNodeKind.Baseline, _ -> 0
            | _, Some commit -> commitDepth Set.empty commit
            | _ -> max 1 node.Sequence

        let depths = ordered |> List.map (fun node -> node.Id, depthFor node) |> Map.ofList

        let lanes =
            ordered
            |> List.groupBy (fun node -> Map.find node.Id depths)
            |> List.collect (fun (_, nodesAtDepth) ->
                nodesAtDepth
                |> List.sortBy (fun node -> node.Sequence, node.Commit |> Option.map CommitOid.value)
                |> List.mapi (fun lane node -> node.Id, lane))
            |> Map.ofList

        let maxRows =
            lanes
            |> Map.toList
            |> List.map snd
            |> function
                | [] -> 1
                | values -> 1 + List.max values

        let positions =
            ordered
            |> List.map (fun node ->
                let commit = node.Commit

                let incoming =
                    commit
                    |> Option.bind (fun value -> Map.tryFind value parentsByChild)
                    |> Option.defaultValue []

                { Node = node
                  X = 24.0 + float (Map.find node.Id depths) * step
                  Y = 24.0 + float (Map.find node.Id lanes) * rowStep
                  Width = nodeWidth
                  Height = nodeHeight
                  IsChampion = commit = snapshot.Champion
                  IsActiveHead = commit |> Option.exists (fun value -> Set.contains value snapshot.ActiveHeads)
                  IsSynthesis = incoming |> List.exists (fun edge -> edge.Kind = ExperimentKind.Synthesis) })

        let positionForCommit commit =
            positions |> List.tryFind (fun item -> item.Node.Commit = Some commit)

        let edges =
            authoritativeEdges
            |> List.choose (fun edge ->
                match positionForCommit edge.Parent, positionForCommit edge.Child with
                | Some parent, Some child ->
                    Some
                        { From =
                            { X = parent.X + parent.Width
                              Y = parent.Y + parent.Height / 2.0 }
                          To =
                            { X = child.X
                              Y = child.Y + child.Height / 2.0 }
                          Kind = edge.Kind
                          Role = edge.Role }
                | _ -> None)

        let values = ordered |> List.choose _.Metric

        let minimum, maximum =
            match values with
            | [] -> None, None
            | _ ->
                let minValue = values |> List.min
                let maxValue = values |> List.max

                if minValue = maxValue then
                    Some(minValue - 1M), Some(maxValue + 1M)
                else
                    let padding = (maxValue - minValue) * 0.12M
                    Some(minValue - padding), Some(maxValue + padding)

        let primaryParent commit =
            parentsByChild
            |> Map.tryFind commit
            |> Option.bind (fun edges ->
                edges
                |> List.tryFind (fun edge -> edge.Role = ExperimentParentRole.Primary)
                |> Option.orElseWith (fun () -> List.tryHead edges))
            |> Option.map _.Parent

        let rec primaryLineage visited commit =
            if Set.contains commit visited then
                visited
            else
                match primaryParent commit with
                | None -> Set.add commit visited
                | Some parent -> primaryLineage (Set.add commit visited) parent

        let selectedCommit =
            selected
            |> Option.bind (fun nodeId ->
                ordered |> List.tryFind (fun node -> node.Id = nodeId) |> Option.bind _.Commit)

        let plottedCommits =
            [ snapshot.Champion; selectedCommit ]
            |> List.choose id
            |> List.fold (fun state commit -> Set.union state (primaryLineage Set.empty commit)) Set.empty

        let plottedIds =
            ordered
            |> List.choose (fun node ->
                node.Commit
                |> Option.filter (fun commit -> Set.contains commit plottedCommits)
                |> Option.map (fun _ -> node.Id))
            |> Set.ofList
            |> Set.add BaselineNode

        let scores =
            let scorePoints =
                Evolution.scorePoints snapshot.Run.Direction snapshot.Run.BaselineScore snapshot.Nodes

            scorePoints
            |> List.filter (fun point -> Set.contains point.NodeId plottedIds)
            |> List.map (fun point ->
                let x =
                    positions
                    |> List.tryFind (fun position -> position.Node.Id = point.NodeId)
                    |> Option.map (fun position -> position.X + nodeWidth / 2.0)
                    |> Option.defaultValue (24.0 + float point.Sequence * step + nodeWidth / 2.0)

                let metricY =
                    match minimum, maximum, point.Metric with
                    | Some minValue, Some maxValue, Some value -> Some(scoreY minValue maxValue 172.0 value)
                    | _ -> None

                let retainedY =
                    match minimum, maximum, point.RetainedScore with
                    | Some minValue, Some maxValue, Some value -> Some(scoreY minValue maxValue 172.0 value)
                    | _ -> None

                { NodeId = point.NodeId
                  X = x
                  MetricY = metricY
                  RetainedY = retainedY
                  Metric = point.Metric
                  RetainedScore = point.RetainedScore })

        let maxDepth = depths |> Map.values |> Seq.max

        { Width = max 760.0 (48.0 + float (maxDepth + 1) * step)
          GraphHeight = max 188.0 (48.0 + float maxRows * rowStep)
          ChartHeight = 208.0
          Nodes = positions
          Edges = edges
          Scores = scores
          AxisMinimum = minimum
          AxisMaximum = maximum }

    let build (snapshot: EvolutionSnapshot) : EvolutionLayout = buildForSelection snapshot None
