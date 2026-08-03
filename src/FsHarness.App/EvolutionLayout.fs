namespace FsHarness.App

open FsHarness.Core

type EvolutionPoint = { X: float; Y: float }

type EvolutionNodeLayout =
    { Node: EvolutionNode
      X: float
      Y: float
      Width: float
      Height: float }

type EvolutionEdgeLayout =
    { From: EvolutionPoint
      To: EvolutionPoint }

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
    let private centerY = 92.0
    let private sideOffset = 58.0

    let private outcomeIsTerminal outcome =
        match outcome with
        | EvolutionOutcome.Accepted
        | EvolutionOutcome.Active _ -> false
        | _ -> true

    let private scoreY minimum maximum height value =
        let range = maximum - minimum
        let fraction = float ((maximum - value) / range)
        28.0 + fraction * (height - 56.0)

    let build (snapshot: EvolutionSnapshot) : EvolutionLayout =
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

        let ordered = allNodes |> List.sortBy _.Sequence

        let positions =
            ordered
            |> List.map (fun node ->
                let lane =
                    if node.Kind = EvolutionNodeKind.Baseline || not (outcomeIsTerminal node.Outcome) then
                        0.0
                    else if node.Sequence % 2 = 0 then
                        -1.0
                    else
                        1.0

                { Node = node
                  X = 24.0 + float node.Sequence * step
                  Y = centerY + lane * sideOffset
                  Width = nodeWidth
                  Height = nodeHeight })

        let positionForCommit commit =
            positions |> List.tryFind (fun item -> item.Node.Commit = Some commit)

        let edges =
            positions
            |> List.choose (fun item ->
                if item.Node.Kind = EvolutionNodeKind.Baseline then
                    None
                else
                    let parent =
                        item.Node.Parent
                        |> Option.bind positionForCommit
                        |> Option.defaultValue positions.Head

                    Some
                        { From =
                            { X = parent.X + parent.Width
                              Y = parent.Y + parent.Height / 2.0 }
                          To =
                            { X = item.X
                              Y = item.Y + item.Height / 2.0 } })

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

        let scores =
            let scorePoints =
                Evolution.scorePoints snapshot.Run.Direction snapshot.Run.BaselineScore snapshot.Nodes

            scorePoints
            |> List.map (fun point ->
                let x = 24.0 + float point.Sequence * step + nodeWidth / 2.0

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

        { Width = max 760.0 (48.0 + float (List.length ordered) * step)
          GraphHeight = 188.0
          ChartHeight = 208.0
          Nodes = positions
          Edges = edges
          Scores = scores
          AxisMinimum = minimum
          AxisMaximum = maximum }
