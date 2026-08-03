namespace FsHarness.Tests

open System
open System.Diagnostics
open System.IO
open System.Threading
open FsHarness.Core
open FsHarness.Infrastructure
open Xunit

module RuntimeIntegrationTests =
    let private deleteTree path =
        if Directory.Exists path then
            Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
            |> Seq.iter (fun entry ->
                try
                    File.SetAttributes(entry, FileAttributes.Normal)
                with _ ->
                    ())

            File.SetAttributes(path, FileAttributes.Normal)
            Directory.Delete(path, true)

    let private getResult result =
        match result with
        | Ok value -> value
        | Error error -> failwith $"Unexpected error: {error.Summary}"

    let private runGit workingDirectory arguments =
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- "git"
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        arguments |> List.iter startInfo.ArgumentList.Add
        use childProcess = Process.Start startInfo
        let stdout = childProcess.StandardOutput.ReadToEnd()
        let stderr = childProcess.StandardError.ReadToEnd()
        childProcess.WaitForExit()

        if childProcess.ExitCode <> 0 then
            failwith $"git failed: {stderr}"

        stdout.Trim()

    [<Fact>]
    let ``runtime retains one strict fake improvement without touching source`` () =
        let temporary =
            Path.Combine(Path.GetTempPath(), "fsharness-runtime-tests", Guid.NewGuid().ToString("N"))

        let source = Path.Combine(temporary, "source")
        let dataRoot = Path.Combine(temporary, "data")
        Directory.CreateDirectory(Path.Combine(source, "src")) |> ignore

        try
            runGit source [ "init"; "--quiet" ] |> ignore
            File.WriteAllText(Path.Combine(source, "src", "score.txt"), "1")
            runGit source [ "add"; "." ] |> ignore

            runGit
                source
                [ "-c"
                  "user.name=Fixture"
                  "-c"
                  "user.email=fixture@example.test"
                  "commit"
                  "--quiet"
                  "-m"
                  "baseline" ]
            |> ignore

            let sourceHead = runGit source [ "rev-parse"; "HEAD" ]
            let codex = AdapterFixture.executable "FsHarness.FakeCodex" "FsHarness.FakeCodex"

            let evaluator =
                AdapterFixture.executable "FsHarness.FakeEvaluator" "FsHarness.FakeEvaluator"

            use runtime = new HarnessRuntime(dataRoot, codex)

            let inspection =
                runtime.InspectSource(source, CancellationToken.None)
                |> Async.RunSynchronously
                |> getResult

            let config =
                { SchemaVersion = HarnessConfig.currentSchemaVersion
                  SourcePath = source
                  BaseCommit = inspection.Head
                  Objective = "Increase the deterministic score."
                  EditablePaths = [ "src/**" ]
                  SeedPatches = []
                  Evaluator =
                    { Executable = evaluator
                      Arguments = []
                      WorkingDirectory = "."
                      Timeout = TimeSpan.FromSeconds 10.0
                      RequiredConstraints = [ "build"; "tests" ]
                      MaxInconclusiveRetries = 2 }
                  Metric =
                    { Name = "primary"
                      Direction = Maximize
                      MinDelta = 0M
                      Target = None
                      Comparison = RetainedScore }
                  Model = Defaults.model
                  PromptProfile = Defaults.promptProfile
                  Budgets =
                    { Defaults.budgets with
                        MaxExperiments = 1
                        CodexTimeout = TimeSpan.FromSeconds 10.0 }
                  PromotionMode = AutoWhenStrictlyBetter }

            runtime.Prepare(config, CancellationToken.None)
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            runtime.Start() |> getResult |> ignore

            let completed =
                SpinWait.SpinUntil(
                    (fun () ->
                        match runtime.State with
                        | Some state ->
                            match state.Status with
                            | Completed _ -> true
                            | _ -> false
                        | None -> false),
                    TimeSpan.FromSeconds 20.0
                )

            Assert.True(completed, "The fake one-candidate run did not complete in time.")
            let finalState = runtime.State |> Option.get
            Assert.Equal(1, finalState.Attempted)
            Assert.Equal(1, finalState.AcceptedCount)
            Assert.Equal(2M, finalState.FrontierScore)
            Assert.Equal(120L, TokenUsage.rawTotal finalState.Usage)
            Assert.Equal("1", File.ReadAllText(Path.Combine(source, "src", "score.txt")))
            Assert.Equal(sourceHead, runGit source [ "rev-parse"; "HEAD" ])

            let history = runtime.History 100 |> getResult |> List.rev
            let pending = history |> List.findIndex (fun event -> event.Kind = "AcceptPending")
            let accepted = history |> List.findIndex (fun event -> event.Kind = "Accepted")
            Assert.True(pending < accepted)

            let runs =
                runtime.ListEvolutionRuns(CancellationToken.None)
                |> Async.RunSynchronously
                |> getResult

            let listed = Assert.Single runs
            Assert.Equal(runtime.State.Value.Id, listed.Id)

            let snapshot =
                runtime.LoadEvolution(listed.Id, CancellationToken.None)
                |> Async.RunSynchronously
                |> getResult

            Assert.Equal(1, snapshot.Nodes.Length)
            Assert.Equal(1, snapshot.Run.AcceptedCount)
            Assert.Equal(2M, snapshot.Run.FrontierScore.Value)
            Assert.Equal(sourceHead, CommitOid.value snapshot.Run.BaselineCommit.Value)
            Assert.Equal(sourceHead, CommitOid.value snapshot.Nodes.Head.Parent.Value)
            Assert.Contains(snapshot.Nodes, fun node -> node.Outcome = EvolutionOutcome.Accepted)
        finally
            deleteTree temporary
