namespace FsHarness.Core

[<Struct>]
type AgentId = private AgentId of string

[<RequireQualifiedAccess>]
module AgentId =
    let create value = AgentId value
    let value (AgentId value) = value

[<RequireQualifiedAccess>]
type AgentRole =
    | Planner
    | Implementer
    | Evaluator
    | Reviewer
    | Researcher

type AgentAssignment =
    { AgentId: AgentId
      Role: AgentRole
      WorkItemId: WorkItemId
      Parent: CommitOid
      Objective: string }

type AgentCandidate =
    { Assignment: AgentAssignment
      Commit: CommitOid
      Metric: decimal option
      ChangedPaths: Set<string>
      Summary: ExperimentSummary }

type AgentAggregation =
    { OrderedCandidates: AgentCandidate list
      Preferred: AgentCandidate option
      ConflictingPaths: Map<string, AgentId list> }

[<RequireQualifiedAccess>]
module AgentAggregation =
    let private metricOrder direction candidate =
        match direction, candidate.Metric with
        | Maximize, Some metric -> 0, -metric
        | Minimize, Some metric -> 0, metric
        | _, None -> 1, 0M

    let aggregate direction candidates =
        let ordered =
            candidates
            |> List.sortBy (fun candidate ->
                metricOrder direction candidate,
                AgentId.value candidate.Assignment.AgentId,
                CommitOid.value candidate.Commit)

        let conflicts =
            candidates
            |> List.collect (fun candidate ->
                candidate.ChangedPaths
                |> Set.toList
                |> List.map (fun path -> path, candidate.Assignment.AgentId))
            |> List.groupBy fst
            |> List.choose (fun (path, entries) ->
                let agents = entries |> List.map snd |> List.distinct |> List.sortBy AgentId.value

                if agents.Length > 1 then Some(path, agents) else None)
            |> Map.ofList

        { OrderedCandidates = ordered
          Preferred = List.tryHead ordered
          ConflictingPaths = conflicts }
