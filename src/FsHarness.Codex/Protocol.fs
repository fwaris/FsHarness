namespace FsHarness.Codex

open System
open System.Text.Json
open FsHarness.Core

type ItemEvent =
    { EventKind: string
      Id: string option
      ItemType: string option
      Text: string option
      Raw: string }

type ProtocolEvent =
    | ThreadStarted of string
    | TurnStarted
    | Item of ItemEvent
    | TurnCompleted of TokenUsage
    | TurnFailed of string option
    | ErrorEvent of string option
    | Unknown of eventType: string option * raw: string
    | Malformed of raw: string * error: string

[<RequireQualifiedAccess>]
module Protocol =
    let private tryProperty (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value -> Some value
        | false, _ -> None

    let private tryString (name: string) (element: JsonElement) : string option =
        tryProperty name element
        |> Option.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then
                value.GetString() |> Option.ofObj
            else
                None)

    let private tryInt64 (name: string) (element: JsonElement) : int64 option =
        tryProperty name element
        |> Option.bind (fun value ->
            match value.TryGetInt64() with
            | true, number -> Some number
            | false, _ -> None)

    let parseLine (line: string) =
        try
            use document = JsonDocument.Parse line
            let root = document.RootElement
            let eventType = tryString "type" root

            match eventType with
            | Some "thread.started" ->
                match tryString "thread_id" root with
                | Some threadId -> ThreadStarted threadId
                | None -> Malformed(line, "thread.started did not contain thread_id")
            | Some "turn.started" -> TurnStarted
            | Some "turn.completed" ->
                match tryProperty "usage" root with
                | None -> Malformed(line, "turn.completed did not contain usage")
                | Some usage ->
                    { InputTokens = tryInt64 "input_tokens" usage |> Option.defaultValue 0L
                      CachedInputTokens = tryInt64 "cached_input_tokens" usage |> Option.defaultValue 0L
                      OutputTokens = tryInt64 "output_tokens" usage |> Option.defaultValue 0L
                      ReasoningOutputTokens = tryInt64 "reasoning_output_tokens" usage |> Option.defaultValue 0L }
                    |> TokenUsage.normalize
                    |> TurnCompleted
            | Some "turn.failed" -> TurnFailed(tryString "message" root)
            | Some "error" -> ErrorEvent(tryString "message" root)
            | Some itemKind when itemKind.StartsWith("item.", StringComparison.Ordinal) ->
                let item = tryProperty "item" root

                Item
                    { EventKind = itemKind
                      Id = item |> Option.bind (tryString "id")
                      ItemType = item |> Option.bind (tryString "type")
                      Text = item |> Option.bind (tryString "text")
                      Raw = line }
            | value -> Unknown(value, line)
        with error ->
            Malformed(line, error.Message)

    let private requiredString (name: string) (root: JsonElement) =
        match tryString name root with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Ok value
        | _ -> Error $"Missing required string '{name}'."

    let parseExperimentSummary (json: string) =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            let notes =
                match tryProperty "validationNotes" root with
                | Some values when values.ValueKind = JsonValueKind.Array ->
                    values.EnumerateArray()
                    |> Seq.choose (fun value ->
                        if value.ValueKind = JsonValueKind.String then
                            value.GetString() |> Option.ofObj
                        else
                            None)
                    |> List.ofSeq
                    |> Ok
                | _ -> Error "Missing required string array 'validationNotes'."

            let hypothesisFamily =
                tryString "hypothesisFamily" root
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.defaultValue "general"

            match
                requiredString "hypothesis" root,
                requiredString "changeSummary" root,
                requiredString "expectedEffect" root,
                notes,
                requiredString "reusableLesson" root
            with
            | Ok hypothesis, Ok change, Ok effect, Ok validationNotes, Ok lesson ->
                Ok
                    { HypothesisFamily = hypothesisFamily
                      Hypothesis = hypothesis
                      ChangeSummary = change
                      ExpectedEffect = effect
                      ValidationNotes = validationNotes
                      ReusableLesson = lesson }
            | results ->
                let errors =
                    match results with
                    | first, second, third, fourth, fifth ->
                        [ first |> Result.map ignore
                          second |> Result.map ignore
                          third |> Result.map ignore
                          fourth |> Result.map ignore
                          fifth |> Result.map ignore ]
                        |> List.choose (function
                            | Error error -> Some error
                            | Ok() -> None)

                Error(String.concat " " errors)
        with error ->
            Error error.Message

    let parseModelCatalog (json: string) =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            match tryProperty "models" root with
            | None -> Error "Model catalog did not contain a models array."
            | Some models when models.ValueKind <> JsonValueKind.Array ->
                Error "Model catalog models value was not an array."
            | Some models ->
                let effort (value: string) =
                    match value with
                    | "low" -> Some ReasoningEffort.Low
                    | "medium" -> Some ReasoningEffort.Medium
                    | "high" -> Some ReasoningEffort.High
                    | "xhigh" -> Some ReasoningEffort.XHigh
                    | "max" -> Some ReasoningEffort.Max
                    | _ -> None

                models.EnumerateArray()
                |> Seq.choose (fun model ->
                    tryString "slug" model
                    |> Option.map (fun slug ->
                        let efforts =
                            match tryProperty "supported_reasoning_levels" model with
                            | Some values when values.ValueKind = JsonValueKind.Array ->
                                values.EnumerateArray()
                                |> Seq.choose (fun entry -> tryString "effort" entry |> Option.bind effort)
                                |> List.ofSeq
                            | _ -> []

                        { Id = slug
                          SupportedReasoningEfforts = efforts }))
                |> List.ofSeq
                |> Ok
        with error ->
            Error error.Message
