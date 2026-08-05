namespace FsHarness.Core

open System

[<Struct>]
type KnowledgeEntityId = private KnowledgeEntityId of Guid

[<RequireQualifiedAccess>]
module KnowledgeEntityId =
    let create () = KnowledgeEntityId(Guid.NewGuid())
    let ofGuid value = KnowledgeEntityId value
    let value (KnowledgeEntityId value) = value
    let text (KnowledgeEntityId value) = string value

[<Struct>]
type KnowledgeSourceId = private KnowledgeSourceId of Guid

[<RequireQualifiedAccess>]
module KnowledgeSourceId =
    let create () = KnowledgeSourceId(Guid.NewGuid())
    let ofGuid value = KnowledgeSourceId value
    let value (KnowledgeSourceId value) = value
    let text (KnowledgeSourceId value) = string value

[<Struct>]
type KnowledgeClaimId = private KnowledgeClaimId of Guid

[<RequireQualifiedAccess>]
module KnowledgeClaimId =
    let create () = KnowledgeClaimId(Guid.NewGuid())
    let ofGuid value = KnowledgeClaimId value
    let value (KnowledgeClaimId value) = value
    let text (KnowledgeClaimId value) = string value

[<RequireQualifiedAccess>]
type KnowledgeEntityKind =
    | Repository
    | File
    | Symbol
    | Experiment
    | Metric
    | Hypothesis
    | Constraint
    | Concept

type KnowledgeEntity =
    { Id: KnowledgeEntityId
      Kind: KnowledgeEntityKind
      CanonicalName: string
      Attributes: Map<string, string> }

[<RequireQualifiedAccess>]
type KnowledgeSourceKind =
    | Artifact
    | Evaluation
    | Commit
    | AgentObservation
    | UserStatement

type KnowledgeSource =
    { Id: KnowledgeSourceId
      RunId: RunId
      ExperimentId: ExperimentId option
      Kind: KnowledgeSourceKind
      Location: string
      Sha256: string option
      CapturedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
type KnowledgeValue =
    | Entity of KnowledgeEntityId
    | Text of string
    | Number of decimal
    | Flag of bool

type KnowledgeClaim =
    { Id: KnowledgeClaimId
      RunId: RunId
      Subject: KnowledgeEntityId
      Predicate: string
      Object: KnowledgeValue
      Confidence: decimal
      Sources: Set<KnowledgeSourceId>
      Supersedes: KnowledgeClaimId option
      CreatedAt: DateTimeOffset }

type KnowledgeGraph =
    { Entities: Map<KnowledgeEntityId, KnowledgeEntity>
      Aliases: Map<string, Set<KnowledgeEntityId>>
      Sources: Map<KnowledgeSourceId, KnowledgeSource>
      Claims: Map<KnowledgeClaimId, KnowledgeClaim> }

type KnowledgeHit =
    { Claim: KnowledgeClaim
      Subject: KnowledgeEntity
      Sources: KnowledgeSource list
      Score: decimal }

[<RequireQualifiedAccess>]
module KnowledgeGraph =
    let empty =
        { Entities = Map.empty
          Aliases = Map.empty
          Sources = Map.empty
          Claims = Map.empty }

    let normalizeAlias (value: string) = value.Trim().ToLowerInvariant()

    let addEntity (entity: KnowledgeEntity) (graph: KnowledgeGraph) =
        if String.IsNullOrWhiteSpace entity.CanonicalName then
            Error "Knowledge entities require a canonical name."
        else
            let aliases =
                graph.Aliases
                |> Map.change (normalizeAlias entity.CanonicalName) (fun current ->
                    current |> Option.defaultValue Set.empty |> Set.add entity.Id |> Some)

            Ok
                { graph with
                    Entities = graph.Entities |> Map.add entity.Id entity
                    Aliases = aliases }

    let addAlias entityId alias (graph: KnowledgeGraph) =
        if not (Map.containsKey entityId graph.Entities) then
            Error "An alias cannot refer to an unknown knowledge entity."
        elif String.IsNullOrWhiteSpace alias then
            Error "Knowledge aliases cannot be empty."
        else
            Ok
                { graph with
                    Aliases =
                        graph.Aliases
                        |> Map.change (normalizeAlias alias) (fun current ->
                            current |> Option.defaultValue Set.empty |> Set.add entityId |> Some) }

    let addSource (source: KnowledgeSource) (graph: KnowledgeGraph) =
        if String.IsNullOrWhiteSpace source.Location then
            Error "Knowledge sources require a location."
        else
            Ok
                { graph with
                    Sources = graph.Sources |> Map.add source.Id source }

    let addClaim (claim: KnowledgeClaim) (graph: KnowledgeGraph) =
        let objectEntityExists =
            match claim.Object with
            | KnowledgeValue.Entity entityId -> Map.containsKey entityId graph.Entities
            | _ -> true

        let sourcesExist =
            not (Set.isEmpty claim.Sources)
            && claim.Sources
               |> Set.forall (fun sourceId -> Map.containsKey sourceId graph.Sources)

        let supersededClaimExists =
            claim.Supersedes
            |> Option.forall (fun claimId -> Map.containsKey claimId graph.Claims)

        if not (Map.containsKey claim.Subject graph.Entities) then
            Error "A claim cannot refer to an unknown subject entity."
        elif not objectEntityExists then
            Error "A claim cannot refer to an unknown object entity."
        elif String.IsNullOrWhiteSpace claim.Predicate then
            Error "Knowledge claims require a predicate."
        elif claim.Confidence < 0M || claim.Confidence > 1M then
            Error "Knowledge claim confidence must be between zero and one."
        elif not sourcesExist then
            Error "Knowledge claims require at least one known provenance source."
        elif not supersededClaimExists then
            Error "A claim cannot supersede an unknown claim."
        else
            Ok
                { graph with
                    Claims = graph.Claims |> Map.add claim.Id claim }

    let private valueText (graph: KnowledgeGraph) value =
        match value with
        | KnowledgeValue.Entity entityId ->
            graph.Entities
            |> Map.tryFind entityId
            |> Option.map _.CanonicalName
            |> Option.defaultValue (KnowledgeEntityId.text entityId)
        | KnowledgeValue.Text text -> text
        | KnowledgeValue.Number number -> string number
        | KnowledgeValue.Flag value -> string value

    let search (query: string) limit (graph: KnowledgeGraph) =
        let terms =
            query.Split([| ' '; '\t'; '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map normalizeAlias
            |> Array.distinct

        let aliasesByEntity =
            graph.Aliases
            |> Map.toSeq
            |> Seq.collect (fun (alias, entityIds) -> entityIds |> Seq.map (fun entityId -> entityId, alias))
            |> Seq.groupBy fst
            |> Seq.map (fun (entityId, entries) -> entityId, entries |> Seq.map snd |> String.concat " ")
            |> Map.ofSeq

        let score claim subject =
            let searchable =
                [ subject.CanonicalName
                  aliasesByEntity |> Map.tryFind subject.Id |> Option.defaultValue String.Empty
                  claim.Predicate
                  valueText graph claim.Object ]
                |> String.concat " "
                |> normalizeAlias

            let lexical =
                terms |> Array.sumBy (fun term -> if searchable.Contains term then 1M else 0M)

            lexical + claim.Confidence

        graph.Claims
        |> Map.values
        |> Seq.choose (fun claim ->
            graph.Entities
            |> Map.tryFind claim.Subject
            |> Option.map (fun subject ->
                let relevance = score claim subject

                { Claim = claim
                  Subject = subject
                  Sources =
                    claim.Sources
                    |> Set.toList
                    |> List.choose (fun sourceId -> Map.tryFind sourceId graph.Sources)
                  Score = relevance }))
        |> Seq.filter (fun hit -> hit.Score > hit.Claim.Confidence || Array.isEmpty terms)
        |> Seq.sortBy (fun hit -> -hit.Score, KnowledgeClaimId.text hit.Claim.Id)
        |> Seq.truncate (max 0 limit)
        |> List.ofSeq

[<Struct>]
type GraphNodeId = private GraphNodeId of string

[<RequireQualifiedAccess>]
module GraphNodeId =
    let create value = GraphNodeId value
    let value (GraphNodeId value) = value

[<Struct>]
type GraphEdgeId = private GraphEdgeId of string

[<RequireQualifiedAccess>]
module GraphEdgeId =
    let create value = GraphEdgeId value
    let value (GraphEdgeId value) = value

[<RequireQualifiedAccess>]
type GraphNodeKind =
    | Entity
    | Claim
    | Source
    | Artifact
    | AgentRun
    | Evaluation
    | Task
    | Commit
    | Metric

[<RequireQualifiedAccess>]
type GraphRelationKind =
    | Mentions
    | Supports
    | Contradicts
    | DerivedFrom
    | Produced
    | Evaluates
    | Revises
    | Supersedes
    | DependsOn
    | ParentOf
    | ResolvedTo

[<RequireQualifiedAccess>]
type GraphProvenance =
    | Sourced of Set<KnowledgeSourceId>
    | Inference of rationale: string

type RepositoryGraphNode =
    { Id: GraphNodeId
      Kind: GraphNodeKind
      CanonicalName: string
      Attributes: Map<string, string>
      Version: int
      OriginRunId: RunId
      CreatedAt: DateTimeOffset }

type RepositoryGraphEdge =
    { Id: GraphEdgeId
      From: GraphNodeId
      Relation: GraphRelationKind
      To: GraphNodeId
      Confidence: decimal
      Provenance: GraphProvenance
      OriginRunId: RunId
      ValidFrom: DateTimeOffset
      ValidTo: DateTimeOffset option }

type GraphUpdate =
    { ProjectId: string
      RunId: RunId
      AgentId: string
      IdempotencyKey: string
      Nodes: RepositoryGraphNode list
      Edges: RepositoryGraphEdge list }

type GraphQuery =
    { Seeds: Set<GraphNodeId>
      MaxHops: int
      MaxEdges: int
      MaxCharacters: int
      AllowedRelations: Set<GraphRelationKind>
      AsOf: DateTimeOffset option
      IncludeConflicts: bool }

type GraphContext =
    { Nodes: RepositoryGraphNode list
      Edges: RepositoryGraphEdge list
      SourceIds: Set<KnowledgeSourceId>
      MissingEvidence: GraphEdgeId list
      Truncated: bool
      Serialized: string }

type RepositoryKnowledgeGraph =
    { ProjectId: string
      Nodes: Map<GraphNodeId, RepositoryGraphNode list>
      Edges: Map<GraphEdgeId, RepositoryGraphEdge>
      AppliedUpdates: Set<string> }

[<RequireQualifiedAccess>]
module RepositoryKnowledgeGraph =
    let empty projectId : RepositoryKnowledgeGraph =
        { ProjectId = projectId
          Nodes = Map.empty
          Edges = Map.empty
          AppliedUpdates = Set.empty }

    let currentNodes (graph: RepositoryKnowledgeGraph) : Map<GraphNodeId, RepositoryGraphNode> =
        graph.Nodes |> Map.map (fun _ versions -> versions |> List.maxBy _.Version)

    let private validateNode (node: RepositoryGraphNode) =
        if String.IsNullOrWhiteSpace(GraphNodeId.value node.Id) then
            Error "Graph nodes require stable identifiers."
        elif String.IsNullOrWhiteSpace node.CanonicalName then
            Error "Graph nodes require canonical names."
        elif node.Version <= 0 then
            Error "Graph node versions must be positive."
        elif
            node.Kind = GraphNodeKind.Artifact
            && (not (Map.containsKey "authoringRun" node.Attributes)
                || not (Map.containsKey "artifactVersion" node.Attributes))
        then
            Error "Artifact nodes require authoringRun and artifactVersion attributes."
        elif
            node.Kind = GraphNodeKind.Evaluation
            && not (Map.containsKey "rubric" node.Attributes)
        then
            Error "Evaluation nodes require a rubric attribute."
        else
            Ok()

    let private validateEdge (endpoints: Set<GraphNodeId>) (edge: RepositoryGraphEdge) =
        let provenanceValid =
            match edge.Provenance with
            | GraphProvenance.Sourced sources -> not (Set.isEmpty sources)
            | GraphProvenance.Inference rationale -> not (String.IsNullOrWhiteSpace rationale)

        if String.IsNullOrWhiteSpace(GraphEdgeId.value edge.Id) then
            Error "Graph edges require stable identifiers."
        elif not (Set.contains edge.From endpoints) || not (Set.contains edge.To endpoints) then
            Error "Graph edge endpoints must exist."
        elif edge.Confidence < 0M || edge.Confidence > 1M then
            Error "Graph edge confidence must be between zero and one."
        elif not provenanceValid then
            Error "Every graph edge requires sources or an explicit inference rationale."
        else
            Ok()

    let validateUpdate (graph: RepositoryKnowledgeGraph) (update: GraphUpdate) =
        if
            String.IsNullOrWhiteSpace update.ProjectId
            || update.ProjectId <> graph.ProjectId
        then
            Error [ "Graph update project identity does not match the repository graph." ]
        elif
            String.IsNullOrWhiteSpace update.AgentId
            || String.IsNullOrWhiteSpace update.IdempotencyKey
        then
            Error [ "Graph updates require agent and idempotency identifiers." ]
        else
            let existing = currentNodes graph |> Map.keys |> Set.ofSeq
            let incoming = update.Nodes |> List.map _.Id |> Set.ofList
            let endpoints = Set.union existing incoming

            let nodeErrors =
                update.Nodes
                |> List.choose (
                    validateNode
                    >> function
                        | Ok() -> None
                        | Error error -> Some error
                )

            let edgeErrors =
                update.Edges
                |> List.choose (
                    validateEdge endpoints
                    >> function
                        | Ok() -> None
                        | Error error -> Some error
                )

            let allEdges = (graph.Edges |> Map.values |> List.ofSeq) @ update.Edges

            let claimErrors =
                update.Nodes
                |> List.choose (fun node ->
                    if
                        node.Kind = GraphNodeKind.Claim
                        && not (allEdges |> List.exists (fun edge -> edge.From = node.Id || edge.To = node.Id))
                    then
                        Some "Claim nodes require sourced evidence or an explicit inference edge."
                    else
                        None)

            let collisionErrors =
                [ for node in update.Nodes do
                      match Map.tryFind node.Id graph.Nodes with
                      | Some versions when
                          versions
                          |> List.exists (fun existing -> existing.Version = node.Version && existing <> node)
                          ->
                          yield $"Graph node '{GraphNodeId.value node.Id}' reuses a version with different content."
                      | _ -> ()

                  for edge in update.Edges do
                      match Map.tryFind edge.Id graph.Edges with
                      | Some existing when existing <> edge ->
                          yield $"Graph edge '{GraphEdgeId.value edge.Id}' is immutable once written."
                      | _ -> () ]

            match nodeErrors @ edgeErrors @ claimErrors @ collisionErrors with
            | [] -> Ok update
            | errors -> Error errors

    let apply (update: GraphUpdate) (graph: RepositoryKnowledgeGraph) =
        if Set.contains update.IdempotencyKey graph.AppliedUpdates then
            Ok graph
        else
            validateUpdate graph update
            |> Result.map (fun valid ->
                let nodes =
                    valid.Nodes
                    |> List.fold
                        (fun state node ->
                            state
                            |> Map.change node.Id (fun versions ->
                                node :: (versions |> Option.defaultValue [])
                                |> List.distinctBy _.Version
                                |> Some))
                        graph.Nodes

                let edges =
                    valid.Edges
                    |> List.fold (fun state edge -> Map.add edge.Id edge state) graph.Edges

                { graph with
                    Nodes = nodes
                    Edges = edges
                    AppliedUpdates = Set.add valid.IdempotencyKey graph.AppliedUpdates })

    let resolveExact kind alias (graph: RepositoryKnowledgeGraph) =
        let normalized = KnowledgeGraph.normalizeAlias alias

        currentNodes graph
        |> Map.values
        |> Seq.filter (fun (node: RepositoryGraphNode) ->
            let names =
                node.CanonicalName
                :: (node.Attributes |> Map.tryFind "aliases" |> Option.toList)

            node.Kind = kind
            && (names
                |> List.collect (fun value -> value.Split('|') |> Array.toList)
                |> List.exists (fun value -> KnowledgeGraph.normalizeAlias value = normalized)))
        |> List.ofSeq

    let private relationText relation =
        match relation with
        | GraphRelationKind.Mentions -> "MENTIONS"
        | GraphRelationKind.Supports -> "SUPPORTS"
        | GraphRelationKind.Contradicts -> "CONTRADICTS"
        | GraphRelationKind.DerivedFrom -> "DERIVED_FROM"
        | GraphRelationKind.Produced -> "PRODUCED"
        | GraphRelationKind.Evaluates -> "EVALUATES"
        | GraphRelationKind.Revises -> "REVISES"
        | GraphRelationKind.Supersedes -> "SUPERSEDES"
        | GraphRelationKind.DependsOn -> "DEPENDS_ON"
        | GraphRelationKind.ParentOf -> "PARENT_OF"
        | GraphRelationKind.ResolvedTo -> "RESOLVED_TO"

    let query (request: GraphQuery) (graph: RepositoryKnowledgeGraph) =
        let current = currentNodes graph
        let asOf = request.AsOf |> Option.defaultValue DateTimeOffset.MaxValue

        let allowedEdges =
            graph.Edges
            |> Map.values
            |> Seq.filter (fun (edge: RepositoryGraphEdge) ->
                Set.contains edge.Relation request.AllowedRelations
                && edge.ValidFrom <= asOf
                && edge.ValidTo |> Option.forall (fun validTo -> validTo > asOf)
                && (request.IncludeConflicts || edge.Relation <> GraphRelationKind.Contradicts))
            |> List.ofSeq

        let rec expand
            hop
            (frontier: Set<GraphNodeId>)
            (visited: Set<GraphNodeId>)
            (selected: RepositoryGraphEdge list)
            =
            if
                hop >= max 0 request.MaxHops
                || Set.isEmpty frontier
                || List.length selected >= request.MaxEdges
            then
                visited, selected
            else
                let additions =
                    allowedEdges
                    |> List.filter (fun (edge: RepositoryGraphEdge) ->
                        Set.contains edge.From frontier || Set.contains edge.To frontier)
                    |> List.filter (fun edge ->
                        not (selected |> List.exists (fun currentEdge -> currentEdge.Id = edge.Id)))
                    |> List.sortBy (fun (edge: RepositoryGraphEdge) -> GraphEdgeId.value edge.Id)
                    |> List.truncate (request.MaxEdges - List.length selected)

                let next =
                    additions
                    |> List.collect (fun edge -> [ edge.From; edge.To ])
                    |> Set.ofList
                    |> Set.filter (fun id -> not (Set.contains id visited))

                expand (hop + 1) next (Set.union visited next) (selected @ additions)

        let visited, edges = expand 0 request.Seeds request.Seeds []
        let nodes = visited |> Set.toList |> List.choose (fun id -> Map.tryFind id current)

        let sources =
            edges
            |> List.fold
                (fun state (edge: RepositoryGraphEdge) ->
                    match edge.Provenance with
                    | GraphProvenance.Sourced ids -> Set.union state ids
                    | GraphProvenance.Inference _ -> state)
                Set.empty

        let missing =
            edges
            |> List.choose (fun (edge: RepositoryGraphEdge) ->
                match edge.Provenance with
                | GraphProvenance.Sourced ids when not (Set.isEmpty ids) -> None
                | _ -> Some edge.Id)

        let lines =
            edges
            |> List.map (fun (edge: RepositoryGraphEdge) ->
                let fromName =
                    current
                    |> Map.tryFind edge.From
                    |> Option.map _.CanonicalName
                    |> Option.defaultValue (GraphNodeId.value edge.From)

                let toName =
                    current
                    |> Map.tryFind edge.To
                    |> Option.map _.CanonicalName
                    |> Option.defaultValue (GraphNodeId.value edge.To)

                $"[{GraphEdgeId.value edge.Id}] {fromName} -{relationText edge.Relation}-> {toName}")

        let mutable remaining = max 0 request.MaxCharacters

        let serialized =
            lines
            |> List.choose (fun line ->
                if remaining <= 0 then
                    None
                else
                    let value =
                        if line.Length <= remaining then
                            line
                        else
                            line.Substring(0, remaining)

                    remaining <- remaining - value.Length
                    Some value)
            |> String.concat Environment.NewLine

        { Nodes = nodes
          Edges = edges
          SourceIds = sources
          MissingEvidence = missing
          Truncated =
            edges.Length >= request.MaxEdges
            || (lines |> List.sumBy (fun line -> line.Length)) > request.MaxCharacters
          Serialized = serialized }
