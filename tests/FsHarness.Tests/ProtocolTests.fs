namespace FsHarness.Tests

open FsHarness.Codex
open FsHarness.Core
open Xunit

module ProtocolTests =
    [<Fact>]
    let ``turn usage parses and preserves subset semantics`` () =
        let line =
            "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":100,\"cached_input_tokens\":80,\"output_tokens\":40,\"reasoning_output_tokens\":30}}"

        match Protocol.parseLine line with
        | TurnCompleted usage ->
            Assert.Equal(140L, TokenUsage.rawTotal usage)
            Assert.Equal(60L, TokenUsage.uncachedTotal usage)
        | other -> Assert.Fail $"Unexpected event: {other}"

    [<Fact>]
    let ``unknown events are forward compatible`` () =
        match Protocol.parseLine "{\"type\":\"future.event\",\"value\":1}" with
        | Unknown(Some "future.event", _) -> ()
        | other -> Assert.Fail $"Unexpected event: {other}"

    [<Fact>]
    let ``malformed lines remain diagnosable`` () =
        match Protocol.parseLine "not-json" with
        | Malformed(raw, error) ->
            Assert.Equal("not-json", raw)
            Assert.NotEmpty error
        | other -> Assert.Fail $"Unexpected event: {other}"

    [<Fact>]
    let ``structured final summary rejects missing fields`` () =
        let valid =
            "{\"hypothesis\":\"h\",\"changeSummary\":\"c\",\"expectedEffect\":\"e\",\"validationNotes\":[\"v\"],\"reusableLesson\":\"l\"}"

        Assert.True(Protocol.parseExperimentSummary valid |> Result.isOk)
        Assert.True(Protocol.parseExperimentSummary "{\"hypothesis\":\"h\"}" |> Result.isError)

    [<Fact>]
    let ``bundled model catalog exposes supported reasoning levels`` () =
        let json =
            "{\"models\":[{\"slug\":\"gpt-5.6-luna\",\"supported_reasoning_levels\":[{\"effort\":\"max\"}]}]}"

        match Protocol.parseModelCatalog json with
        | Ok [ model ] ->
            Assert.Equal("gpt-5.6-luna", model.Id)
            Assert.Contains(ReasoningEffort.Max, model.SupportedReasoningEfforts)
        | other -> Assert.Fail $"Unexpected catalog: {other}"
