namespace FsHarness.FakeEvaluator

open System
open System.IO
open System.Text.Json

module Program =
    [<EntryPoint>]
    let main _ =
        let resultPath = Environment.GetEnvironmentVariable "FSHARNESS_RESULT_PATH"
        let candidatePath = Environment.GetEnvironmentVariable "FSHARNESS_CANDIDATE_PATH"
        let frontierPath = Environment.GetEnvironmentVariable "FSHARNESS_FRONTIER_PATH"

        match resultPath, candidatePath, frontierPath with
        | resultPath, candidatePath, frontierPath when
            not (String.IsNullOrWhiteSpace resultPath)
            && not (String.IsNullOrWhiteSpace candidatePath)
            && not (String.IsNullOrWhiteSpace frontierPath)
            ->
            if Environment.GetEnvironmentVariable "FSHARNESS_FAKE_EVALUATOR_FAIL" = "1" then
                Console.Error.WriteLine "requested evaluator infrastructure failure"
                23
            else
                let readScore root =
                    let scorePath = Path.Combine(root, "src", "score.txt")

                    if File.Exists scorePath then
                        match Decimal.TryParse(File.ReadAllText scorePath) with
                        | true, value -> value
                        | false, _ -> -1M
                    else
                        -1M

                let score = readScore candidatePath
                let frontierScore = readScore frontierPath

                let json =
                    JsonSerializer.Serialize
                        {| schemaVersion = 2
                           status = "complete"
                           constraints = {| build = true; tests = score >= 0M |}
                           metrics =
                            {| primary = score
                               candidate_speed_index = score
                               frontier_speed_index = frontierScore |}
                           summary = "Fake deterministic evaluation."
                           evidence = [| $"candidate={candidatePath}"; $"frontier={frontierPath}" |] |}

                Directory.CreateDirectory(Path.GetDirectoryName resultPath) |> ignore
                let temporary = resultPath + ".tmp"
                File.WriteAllText(temporary, json)
                File.Move(temporary, resultPath, true)
                0
        | _ ->
            Console.Error.WriteLine "Protected evaluator paths were not set"
            2
