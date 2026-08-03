namespace FsHarness.Core

open System

[<Struct>]
type RunAnnotationId = private RunAnnotationId of Guid

[<RequireQualifiedAccess>]
module RunAnnotationId =
    let create () = RunAnnotationId(Guid.NewGuid())
    let ofGuid value = RunAnnotationId value
    let value (RunAnnotationId value) = value
    let text (RunAnnotationId value) = string value

[<RequireQualifiedAccess>]
type AnnotationTarget =
    | Run
    | Experiment of ExperimentId
    | Claim of KnowledgeClaimId

type RunAnnotation =
    { Id: RunAnnotationId
      RunId: RunId
      Target: AnnotationTarget
      Author: string
      Body: string
      CreatedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module RunAnnotation =
    let validate annotation =
        [ if String.IsNullOrWhiteSpace annotation.Author then
              "An annotation requires an author."
          if String.IsNullOrWhiteSpace annotation.Body then
              "An annotation body cannot be empty."
          if annotation.Body.Length > 20_000 then
              "An annotation body cannot exceed 20,000 characters." ]
        |> function
            | [] -> Ok annotation
            | errors -> Error errors
