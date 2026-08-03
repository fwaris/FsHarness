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
