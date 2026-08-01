namespace FsHarness.FakeEvaluator

open System
open System.IO
open System.Text.Json

module Program =
    [<EntryPoint>]
    let main _ =
        match Environment.GetEnvironmentVariable "FSHARNESS_RESULT_PATH" with
        | resultPath when not (String.IsNullOrWhiteSpace resultPath) ->
            if Environment.GetEnvironmentVariable "FSHARNESS_FAKE_EVALUATOR_FAIL" = "1" then
                Console.Error.WriteLine "requested evaluator infrastructure failure"
                23
            else
                let scorePath = Path.Combine(Directory.GetCurrentDirectory(), "src", "score.txt")

                let score =
                    if File.Exists scorePath then
                        match Decimal.TryParse(File.ReadAllText scorePath) with
                        | true, value -> value
                        | false, _ -> -1M
                    else
                        -1M

                let json =
                    JsonSerializer.Serialize
                        {| schemaVersion = 1
                           constraints = {| build = true; tests = score >= 0M |}
                           metrics = {| primary = score |}
                           summary = "Fake deterministic evaluation."
                           evidence = [| $"score={score}" |] |}

                Directory.CreateDirectory(Path.GetDirectoryName resultPath) |> ignore
                let temporary = resultPath + ".tmp"
                File.WriteAllText(temporary, json)
                File.Move(temporary, resultPath, true)
                0
        | _ ->
            Console.Error.WriteLine "FSHARNESS_RESULT_PATH was not set"
            2
