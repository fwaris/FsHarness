namespace FsHarness.FakeEvaluator

open System
open System.IO
open System.Text.Json

module Program =
    [<EntryPoint>]
    let main args =
        let resultPath = Environment.GetEnvironmentVariable "FSHARNESS_RESULT_PATH"
        let candidatePath = Environment.GetEnvironmentVariable "FSHARNESS_CANDIDATE_PATH"
        let frontierPath = Environment.GetEnvironmentVariable "FSHARNESS_FRONTIER_PATH"
        let parentPath = Environment.GetEnvironmentVariable "FSHARNESS_PARENT_PATH"
        let championPath = Environment.GetEnvironmentVariable "FSHARNESS_CHAMPION_PATH"

        let failCountFile =
            args
            |> Array.tryFindIndex ((=) "--fail-count-file")
            |> Option.bind (fun index -> args |> Array.tryItem (index + 1))

        let requestedFailure =
            if args |> Array.contains "--fail" then
                true
            else
                match failCountFile with
                | Some path when File.Exists path ->
                    match Int32.TryParse(File.ReadAllText path) with
                    | true, remaining when remaining > 0 ->
                        File.WriteAllText(path, string (remaining - 1))
                        true
                    | _ -> false
                | _ -> false

        match resultPath, candidatePath, frontierPath, parentPath, championPath with
        | resultPath, candidatePath, frontierPath, parentPath, championPath when
            not (String.IsNullOrWhiteSpace resultPath)
            && not (String.IsNullOrWhiteSpace candidatePath)
            && not (String.IsNullOrWhiteSpace frontierPath)
            && not (String.IsNullOrWhiteSpace parentPath)
            && not (String.IsNullOrWhiteSpace championPath)
            ->
            if
                requestedFailure
                || Environment.GetEnvironmentVariable "FSHARNESS_FAKE_EVALUATOR_FAIL" = "1"
            then
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
