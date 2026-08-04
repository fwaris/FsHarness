namespace FsHarness.Tests

open System
open System.Diagnostics
open System.IO
open System.Threading
open FsHarness.Core
open FsHarness.Infrastructure
open Xunit

module GitStoreTests =
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
            deleteTree path

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

            Assert.Equal(CommitOid.value snapshot.Commit, candidateOid)

            let lineage =
                GitStore.loadLineage store runId CancellationToken.None
                |> Async.RunSynchronously
                |> getResult

            Assert.Equal(Some inspection.Head, lineage.Baseline)
            Assert.Equal(Some inspection.Head, lineage.Frontier)
            Assert.Equal(Some(snapshot.Commit, Some inspection.Head), lineage.Candidates |> Map.tryFind experimentId))

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

    [<Fact>]
    let ``protected seed patch applies only inside generation worktree`` () =
        withTempDirectory (fun directory ->
            let source = createRepository directory
            let seedDirectory = Path.Combine(source, ".fsharness", "seeds")
            Directory.CreateDirectory(seedDirectory) |> ignore

            File.WriteAllText(
                Path.Combine(seedDirectory, "score.patch"),
                "diff --git a/src/score.txt b/src/score.txt\nindex 56a6051..d8263ee 100644\n--- a/src/score.txt\n+++ b/src/score.txt\n@@ -1 +1 @@\n-1\n\\ No newline at end of file\n+2\n\\ No newline at end of file\n"
            )

            runGit source [ "add"; ".fsharness/seeds/score.patch" ] |> ignore

            runGit
                source
                [ "-c"
                  "user.name=Fixture"
                  "-c"
                  "user.email=fixture@example.test"
                  "commit"
                  "--quiet"
                  "-m"
                  "seed" ]
            |> ignore

            let dataRoot = Path.Combine(directory, "data")
            let port = GitStore.create dataRoot |> GitStore.port

            let inspection =
                port.InspectSource source CancellationToken.None
                |> Async.RunSynchronously
                |> getResult

            let runId = RunId.create ()

            port.CreateRun runId inspection CancellationToken.None
            |> Async.RunSynchronously
            |> getResult

            let workspace =
                port.PrepareCandidate runId (ExperimentId.create ()) inspection.Head CancellationToken.None
                |> Async.RunSynchronously
                |> getResult

            port.ApplySeedPatch workspace ".fsharness/seeds/score.patch" CancellationToken.None
            |> Async.RunSynchronously
            |> getResult

            Assert.Equal("2", File.ReadAllText(Path.Combine(workspace.GenerationPath, "src", "score.txt")))
            Assert.Equal("1", File.ReadAllText(Path.Combine(source, "src", "score.txt"))))
