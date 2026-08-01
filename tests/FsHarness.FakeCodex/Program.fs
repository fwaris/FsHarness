namespace FsHarness.FakeCodex

open System
open System.IO
open System.Text.Json
open System.Threading

module Program =
    let private writeJson value =
        JsonSerializer.Serialize value |> Console.WriteLine

    let private argumentAfter name (arguments: string array) =
        arguments
        |> Array.tryFindIndex ((=) name)
        |> Option.bind (fun index -> arguments |> Array.tryItem (index + 1))

    let private runExec arguments =
        let workingDirectory =
            argumentAfter "-C" arguments
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        let mode =
            Environment.GetEnvironmentVariable "FSHARNESS_FAKE_MODE"
            |> Option.ofObj
            |> Option.defaultValue "success"

        if mode = "hang" then
            Thread.Sleep(TimeSpan.FromMinutes 5.0)
            1
        elif mode = "startup-fail" then
            Console.Error.WriteLine "fake startup failure"
            17
        else
            writeJson
                {| ``type`` = "thread.started"
                   thread_id = "fake-thread-1" |}

            writeJson {| ``type`` = "turn.started" |}

            if mode <> "nochange" then
                let scorePath = Path.Combine(workingDirectory, "src", "score.txt")
                Directory.CreateDirectory(Path.GetDirectoryName scorePath) |> ignore

                let current =
                    if File.Exists scorePath then
                        match Decimal.TryParse(File.ReadAllText scorePath) with
                        | true, value -> value
                        | false, _ -> 0M
                    else
                        0M

                File.WriteAllText(scorePath, string (current + 1M))

            if mode = "protected" then
                File.WriteAllText(Path.Combine(workingDirectory, ".gitattributes"), "* text=auto")

            let summary =
                JsonSerializer.Serialize
                    {| hypothesis = "Increment the deterministic fixture score."
                       changeSummary = "Updated src/score.txt."
                       expectedEffect = "Increase the primary metric by one."
                       validationNotes = [| "Fake worker completed." |]
                       reusableLesson = "The score fixture is monotonic." |}

            writeJson
                {| ``type`` = "item.completed"
                   item =
                    {| id = "item-1"
                       ``type`` = "agent_message"
                       text = summary
                       status = "completed" |} |}

            writeJson
                {| ``type`` = "turn.completed"
                   usage =
                    {| input_tokens = 100
                       cached_input_tokens = 40
                       output_tokens = 20
                       reasoning_output_tokens = 10 |} |}

            0

    [<EntryPoint>]
    let main arguments =
        match List.ofArray arguments with
        | [ "--version" ] ->
            Console.WriteLine "codex-cli 0.fake"
            0
        | [ "login"; "status" ] ->
            Console.WriteLine "Logged in using fake credentials"
            0
        | [ "doctor"; "--json" ] ->
            Console.WriteLine "{\"status\":\"ok\"}"
            0
        | [ "debug"; "models"; "--bundled" ] ->
            Console.WriteLine
                "{\"models\":[{\"slug\":\"gpt-5.6-luna\",\"supported_reasoning_levels\":[{\"effort\":\"max\"}]},{\"slug\":\"gpt-5.6-sol\",\"supported_reasoning_levels\":[{\"effort\":\"medium\"}]}]}"

            0
        | "exec" :: _ -> runExec arguments
        | _ ->
            let command = String.concat " " arguments
            Console.Error.WriteLine $"Unsupported fake Codex command: {command}"
            2
