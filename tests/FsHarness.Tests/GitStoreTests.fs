namespace FsHarness.Tests

open System
open System.Diagnostics
open System.IO
open System.Threading
open FsHarness.Core
open FsHarness.Infrastructure
open Xunit

module GitStoreTests =
    let private getResult result =
        match result with
        | Ok value -> value
        | Error error -> failwith $"Unexpected error: {error}"

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
            let command = String.concat " " arguments
            failwith $"git {command} failed: {stderr}"

        stdout.Trim()

    let private createRepository root =
        let source = Path.Combine(root, "source")
        Directory.CreateDirectory(Path.Combine(source, "src")) |> ignore
        runGit source [ "init"; "--quiet" ] |> ignore
        File.WriteAllText(Path.Combine(source, "src", "score.txt"), "1")
        File.WriteAllText(Path.Combine(source, "protected.txt"), "original")
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

        source

    let private withTempDirectory action =
        let path = Path.Combine(Path.GetTempPath(), $"fsharness-git-{Guid.NewGuid():N}")
        Directory.CreateDirectory path |> ignore

        try
            action path
        finally
            Directory.Delete(path, true)

    [<Fact>]
    let ``candidate lineage never mutates dirty source and quarantines protected paths`` () =
        withTempDirectory (fun directory ->
            let source = createRepository directory
            let sourceHead = runGit source [ "rev-parse"; "HEAD" ]
            File.WriteAllText(Path.Combine(source, "local-only.txt"), "user work")

            let dataRoot = Path.Combine(directory, "data")
            let store = GitStore.create dataRoot
            let port = GitStore.port store

            let inspection =
                port.InspectSource source CancellationToken.None
                |> Async.RunSynchronously
                |> getResult

            Assert.True inspection.IsDirty
            let runId = RunId.create ()

            port.CreateRun runId inspection CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let experimentId = ExperimentId.create ()

            let workspace =
                port.PrepareCandidate runId experimentId inspection.Head CancellationToken.None
                |> Async.RunSynchronously
                |> getResult

            File.WriteAllText(Path.Combine(workspace.GenerationPath, "src", "score.txt"), "2")
            File.WriteAllText(Path.Combine(workspace.GenerationPath, ".gitattributes"), "* text=auto")

            let snapshot =
                port.CaptureCandidate runId workspace [ "src/**" ] CancellationToken.None
                |> Async.RunSynchronously
                |> getResult

            Assert.Contains(".gitattributes", snapshot.ProtectedPaths)
            Assert.Contains("src/score.txt", snapshot.ChangedPaths)
            Assert.Equal(sourceHead, runGit source [ "rev-parse"; "HEAD" ])
            Assert.Equal("user work", File.ReadAllText(Path.Combine(source, "local-only.txt")))

            let privateRepo = DataPaths.repository dataRoot runId
            Assert.False(File.Exists(Path.Combine(privateRepo, "objects", "info", "alternates")))

            let candidateRef =
                $"refs/fsharness/runs/{RunId.text runId}/candidates/{ExperimentId.text experimentId}"

            let candidateOid =
                runGit directory [ "--git-dir"; privateRepo; "rev-parse"; candidateRef ]

            Assert.Equal(CommitOid.value snapshot.Commit, candidateOid))

    [<Fact>]
    let ``frontier update is compare and swap`` () =
        withTempDirectory (fun directory ->
            let source = createRepository directory
            let dataRoot = Path.Combine(directory, "data")
            let store = GitStore.create dataRoot
            let port = GitStore.port store

            let inspection =
                port.InspectSource source CancellationToken.None
                |> Async.RunSynchronously
                |> getResult

            let runId = RunId.create ()

            port.CreateRun runId inspection CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let experimentId = ExperimentId.create ()

            let workspace =
                port.PrepareCandidate runId experimentId inspection.Head CancellationToken.None
                |> Async.RunSynchronously
                |> getResult

            File.WriteAllText(Path.Combine(workspace.GenerationPath, "src", "score.txt"), "3")

            let snapshot =
                port.CaptureCandidate runId workspace [ "src/**" ] CancellationToken.None
                |> Async.RunSynchronously
                |> getResult

            port.AdvanceFrontier runId inspection.Head snapshot.Commit CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let stale =
                port.AdvanceFrontier runId inspection.Head snapshot.Commit CancellationToken.None
                |> Async.RunSynchronously

            Assert.True(Result.isError stale))
