namespace FsHarness.Core

open System

type SearchNode =
    { Commit: CommitOid
      Parents: ExperimentParent list
      Metric: decimal option
      HypothesisFamily: string
      Validity: EvaluationValidity
      ChampionDecision: ChampionDecision
      SearchStatus: SearchStatus
      Sequence: int
      SynthesisDepth: int }

type SearchGraphState =
    { Baseline: CommitOid
      Champion: CommitOid
      Direction: MetricDirection
      Nodes: Map<CommitOid, SearchNode> }

type SearchRound =
    { Number: int
      Heads: CommitOid list
      CompletedHeads: Set<CommitOid> }

type SynthesisPair =
    { Primary: CommitOid
      Contributor: CommitOid }

[<RequireQualifiedAccess>]
module GraphSearch =
    let private graphError code summary =
        HarnessError.create code HarnessErrorCategory.Recovery summary

    let private parentCommits node = node.Parents |> List.map _.Commit

    let create baseline direction =
        let baselineNode =
            { Commit = baseline
              Parents = []
              Metric = None
              HypothesisFamily = "baseline"
              Validity = EvaluationValidity.Valid
              ChampionDecision = ChampionDecision.Promoted
              SearchStatus = SearchStatus.ActiveHead
              Sequence = 0
              SynthesisDepth = 0 }

        { Baseline = baseline
          Champion = baseline
          Direction = direction
          Nodes = Map.ofList [ baseline, baselineNode ] }

    let tryFind commit graph = Map.tryFind commit graph.Nodes

    let parents commit graph =
        tryFind commit graph |> Option.map parentCommits |> Option.defaultValue []

    let children parent graph =
        graph.Nodes
        |> Map.values
        |> Seq.filter (fun node -> parentCommits node |> List.contains parent)
        |> Seq.sortBy (fun node -> node.Sequence, CommitOid.value node.Commit)
        |> List.ofSeq

    let ancestors commit graph =
        let rec walk pending visited =
            match pending with
            | [] -> visited
            | current :: remaining when Set.contains current visited -> walk remaining visited
            | current :: remaining ->
                let next = parents current graph
                walk (next @ remaining) (Set.add current visited)

        parents commit graph |> fun roots -> walk roots Set.empty

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

    let primaryLineage commit graph =
        let rec walk visited current collected =
            if Set.contains current visited then
                Error(graphError "search_graph.cycle" "A cycle was detected in the experiment graph.")
            else
                match tryFind current graph with
                | None -> Error(graphError "search_graph.node_missing" "The requested graph node does not exist.")
                | Some node ->
                    match
                        node.Parents
                        |> List.tryFind (fun parent -> parent.Role = ExperimentParentRole.Primary)
                    with
                    | None -> Ok(node :: collected)
                    | Some parent -> walk (Set.add current visited) parent.Commit (node :: collected)

        walk Set.empty commit []

    let validate graph =
        let missingParents =
            graph.Nodes
            |> Map.values
            |> Seq.collect parentCommits
            |> Seq.filter (fun parent -> not (Map.containsKey parent graph.Nodes))
            |> Seq.distinct
            |> List.ofSeq

        let cycles =
            graph.Nodes
            |> Map.keys
            |> Seq.filter (fun commit -> ancestors commit graph |> Set.contains commit)
            |> List.ofSeq

        if not (List.isEmpty missingParents) then
            Error(
                graphError "search_graph.parent_missing" "One or more experiment parents are missing."
                |> HarnessError.withDetail (missingParents |> List.map CommitOid.value |> String.concat ", ")
            )
        elif not (List.isEmpty cycles) then
            Error(graphError "search_graph.cycle" "The experiment graph contains a cycle.")
        elif not (Map.containsKey graph.Champion graph.Nodes) then
            Error(graphError "search_graph.champion_missing" "The champion is not present in the experiment graph.")
        else
            Ok graph

    let addNode node graph =
        if Map.containsKey node.Commit graph.Nodes then
            Error(graphError "search_graph.duplicate" "The experiment commit is already present in the graph.")
        elif List.isEmpty node.Parents then
            Error(graphError "search_graph.parent_required" "Non-baseline experiment nodes require a parent.")
        elif
            node.Parents
            |> List.filter (fun parent -> parent.Role = ExperimentParentRole.Primary)
            |> List.length
            |> (=) 1
            |> not
        then
            Error(graphError "search_graph.primary_parent" "An experiment requires exactly one primary parent.")
        else
            { graph with
                Nodes = Map.add node.Commit node graph.Nodes }
            |> validate

    let promote commit graph =
        if Map.containsKey commit graph.Nodes then
            Ok { graph with Champion = commit }
        else
            Error(graphError "search_graph.promote_missing" "Cannot promote a commit outside the experiment graph.")

    let private isValid node =
        match node.Validity, node.SearchStatus with
        | EvaluationValidity.Valid, (SearchStatus.ActiveHead | SearchStatus.Retained) -> true
        | _ -> false

    let private compareMetric direction left right =
        match left.Metric, right.Metric with
        | Some leftValue, Some rightValue ->
            match direction with
            | Maximize -> compare rightValue leftValue
            | Minimize -> compare leftValue rightValue
        | Some _, None -> -1
        | None, Some _ -> 1
        | None, None -> 0

    let private compareNode direction left right =
        let byMetric = compareMetric direction left right

        if byMetric <> 0 then
            byMetric
        else
            let bySequence = compare left.Sequence right.Sequence

            if bySequence <> 0 then
                bySequence
            else
                StringComparer.Ordinal.Compare(CommitOid.value left.Commit, CommitOid.value right.Commit)

    let rootFamilies commit graph =
        let rec walk current visited =
            if Set.contains current visited || current = graph.Baseline then
                Set.empty
            else
                match tryFind current graph with
                | None -> Set.empty
                | Some node ->
                    let parentValues = parentCommits node

                    if parentValues |> List.contains graph.Baseline then
                        Set.singleton current
                    else
                        parentValues
                        |> List.fold
                            (fun families parent -> Set.union families (walk parent (Set.add current visited)))
                            Set.empty

        walk commit Set.empty

    let selectActiveHeads beamWidth graph =
        let width = max 1 beamWidth

        let candidates =
            graph.Nodes
            |> Map.values
            |> Seq.filter (fun node -> isValid node || node.Commit = graph.Baseline)
            |> Seq.sortWith (compareNode graph.Direction)
            |> List.ofSeq

        let champion = candidates |> List.tryFind (fun node -> node.Commit = graph.Champion)

        let seedNodes, seedFamilies =
            match champion with
            | Some node -> [ node ], rootFamilies node.Commit graph
            | None -> [], Set.empty

        let diverse, represented =
            candidates
            |> List.filter (fun node -> node.Commit <> graph.Champion)
            |> List.fold
                (fun (selected, families) node ->
                    let nodeFamilies = rootFamilies node.Commit graph

                    if
                        List.length selected + List.length seedNodes >= width
                        || Set.isSubset nodeFamilies families
                    then
                        selected, families
                    else
                        node :: selected, Set.union families nodeFamilies)
                ([], seedFamilies)

        let selected = seedNodes @ List.rev diverse
        let selectedIds = selected |> List.map _.Commit |> Set.ofList

        let fillers =
            candidates
            |> List.filter (fun node -> not (Set.contains node.Commit selectedIds))
            |> List.truncate (width - List.length selected)

        (selected @ fillers) |> List.truncate width |> List.map _.Commit

    let startRound number spec graph =
        { Number = number
          Heads = selectActiveHeads spec.BeamWidth graph
          CompletedHeads = Set.empty }

    let pendingHeads round =
        round.Heads
        |> List.filter (fun commit -> not (Set.contains commit round.CompletedHeads))

    let completeHead commit round =
        if round.Heads |> List.contains commit then
            { round with
                CompletedHeads = Set.add commit round.CompletedHeads }
        else
            round

    let roundComplete round = pendingHeads round |> List.isEmpty

    let private orderedPair left right =
        if StringComparer.Ordinal.Compare(CommitOid.value left, CommitOid.value right) <= 0 then
            left, right
        else
            right, left

    let synthesisPairs maxDepth attempted heads graph =
        [ for leftIndex in 0 .. List.length heads - 1 do
              for rightIndex in leftIndex + 1 .. List.length heads - 1 do
                  let left = heads[leftIndex]
                  let right = heads[rightIndex]
                  let pairKey = orderedPair left right
                  let leftNode = tryFind left graph
                  let rightNode = tryFind right graph
                  let leftAncestors = ancestors left graph
                  let rightAncestors = ancestors right graph

                  match leftNode, rightNode with
                  | Some first, Some second when
                      isValid first
                      && isValid second
                      && first.SynthesisDepth < maxDepth
                      && second.SynthesisDepth < maxDepth
                      && not (Set.contains left rightAncestors)
                      && not (Set.contains right leftAncestors)
                      && not (Set.contains pairKey attempted)
                      ->
                      let primary, contributor =
                          if compareNode graph.Direction first second <= 0 then
                              first.Commit, second.Commit
                          else
                              second.Commit, first.Commit

                      yield
                          { Primary = primary
                            Contributor = contributor }
                  | _ -> () ]
        |> List.sortBy (fun pair ->
            let primary = Map.find pair.Primary graph.Nodes
            let contributor = Map.find pair.Contributor graph.Nodes

            let distinctFamily =
                if
                    String.Equals(
                        primary.HypothesisFamily,
                        contributor.HypothesisFamily,
                        StringComparison.OrdinalIgnoreCase
                    )
                then
                    1
                else
                    0

            let values = [ primary.Metric; contributor.Metric ] |> List.choose id

            let worst, best =
                match graph.Direction, values with
                | _, [] -> None, None
                | Maximize, metrics -> Some(List.min metrics), Some(List.max metrics)
                | Minimize, metrics -> Some(List.max metrics), Some(List.min metrics)

            let metricKey =
                match graph.Direction with
                | Maximize -> Option.map (~-) >> Option.defaultValue Decimal.MaxValue
                | Minimize -> Option.defaultValue Decimal.MaxValue

            distinctFamily,
            metricKey worst,
            metricKey best,
            CommitOid.value pair.Primary,
            CommitOid.value pair.Contributor)

    let shouldSynthesize spec ordinarySinceSynthesis consecutiveNonImprovements =
        ordinarySinceSynthesis >= spec.OrdinaryCandidatesPerSynthesis
        || consecutiveNonImprovements >= spec.StagnationTrigger

    let reservedSynthesisSlots spec maxExperiments =
        if maxExperiments < 4 then
            0
        else
            max 1 (int (floor (decimal maxExperiments * spec.MaxSynthesisBudgetFraction)))
