namespace FsHarness.Cli

open System
open System.IO
open System.Text.Json
open System.Threading
open FsHarness.Codex
open FsHarness.Core
open FsHarness.Infrastructure

[<RequireQualifiedAccess>]
module Program =
    type private Options =
        { ConfigPath: string
          DataRoot: string option
          CodexPath: string option
          SummaryPath: string option }

    let private usage () =
        eprintfn "Usage: fsharness run --config <file> [--data-root <dir>] [--codex <exe>] [--summary <file>]"

    let private parse (arguments: string list) =
        let rec loop (options: Options) (remaining: string list) =
            match remaining with
            | [] when not (String.IsNullOrWhiteSpace options.ConfigPath) -> Ok options
            | "--config" :: value :: rest -> loop { options with ConfigPath = value } rest
            | "--data-root" :: value :: rest -> loop { options with DataRoot = Some value } rest
            | "--codex" :: value :: rest -> loop { options with CodexPath = Some value } rest
            | "--summary" :: value :: rest ->
                loop
                    { options with
                        SummaryPath = Some value }
                    rest
            | unknown :: _ -> Error $"Unknown or incomplete argument '{unknown}'."
            | [] -> Error "--config is required."

        match arguments with
        | "run" :: rest ->
            loop
                { ConfigPath = ""
                  DataRoot = None
                  CodexPath = None
                  SummaryPath = None }
                rest
        | _ -> Error "The only supported command is 'run'."

    let private describeError (error: HarnessError) =
        match error.Detail with
        | Some detail -> $"{error.Code}: {error.Summary} {detail}"
        | None -> $"{error.Code}: {error.Summary}"

    let private isTerminal (state: RunState) =
        match state.Status with
        | Completed _
        | Paused _
        | RecoveryRequired _ -> true
        | _ -> false

    let private writeSummary (path: string) (summary: CampaignSummary) =
        let options =
            JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

        let destination = Path.GetFullPath path
        Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore

        let document =
            {| schemaVersion = summary.SchemaVersion
               runId = summary.RunId
               status = summary.Status
               inputTokens = summary.InputTokens
               cachedInputTokens = summary.CachedInputTokens
               uncachedInputTokens = summary.UncachedInputTokens
               outputTokens = summary.OutputTokens
               reasoningTokens = summary.ReasoningTokens
               rawTokens = summary.RawTokens
               attempts = summary.Attempts
               acceptedCandidates = summary.AcceptedCandidates
               evaluatorRetries = summary.EvaluatorRetries
               duplicateHypotheses = summary.DuplicateHypotheses
               tokensToFirstQualifiedImprovement =
                summary.TokensToFirstQualifiedImprovement
                |> Option.map Nullable
                |> Option.defaultValue (Nullable())
               acceptedPercentImprovement = summary.AcceptedPercentImprovement
               tokensPerAcceptedOnePercentSpeedup =
                summary.TokensPerAcceptedOnePercentSpeedup
                |> Option.map Nullable
                |> Option.defaultValue (Nullable()) |}

        File.WriteAllText(destination, JsonSerializer.Serialize(document, options))
        destination

    let private run (options: Options) =
        match ConfigFile.read options.ConfigPath with
        | Error errors ->
            errors |> List.iter (eprintfn "Configuration: %s")
            2
        | Ok config ->
            let codexResolution = CliDiscovery.resolve options.CodexPath
            let dataRoot = options.DataRoot |> Option.defaultValue (DataPaths.root ())
            use runtime = new HarnessRuntime(dataRoot, codexResolution.Executable)

            use activity =
                runtime.Activity.Subscribe(fun item -> eprintfn "[%s] %s" (item.Timestamp.ToString("o")) item.Message)

            use cancellation =
                new CancellationTokenSource(config.Budgets.MaxDuration + TimeSpan.FromMinutes 5.0)

            match runtime.Prepare(config, cancellation.Token) |> Async.RunSynchronously with
            | Error error ->
                eprintfn "%s" (describeError error)
                3
            | Ok report ->
                eprintfn "Prepared run %s at score %M." (RunId.text report.RunId) report.BaselineScore

                match runtime.Start() with
                | Error error ->
                    eprintfn "%s" (describeError error)
                    4
                | Ok _ ->
                    let completed =
                        SpinWait.SpinUntil(
                            (fun () -> runtime.State |> Option.exists isTerminal),
                            config.Budgets.MaxDuration + TimeSpan.FromMinutes 2.0
                        )

                    if not completed then
                        runtime.StopNow()
                        eprintfn "Campaign did not reach a terminal state before the headless timeout."
                        5
                    else
                        match runtime.CampaignSummary() with
                        | Error error ->
                            eprintfn "%s" (describeError error)
                            6
                        | Ok summary ->
                            let summaryPath =
                                options.SummaryPath
                                |> Option.defaultValue (Path.Combine(report.DataDirectory, "campaign-summary.json"))

                            let written = writeSummary summaryPath summary
                            printfn "%s" written

                            match runtime.State |> Option.map _.Status with
                            | Some(RecoveryRequired _)
                            | Some(Paused _) -> 7
                            | _ -> 0

    [<EntryPoint>]
    let main arguments =
        match parse (List.ofArray arguments) with
        | Error error ->
            eprintfn "%s" error
            usage ()
            1
        | Ok options -> run options
