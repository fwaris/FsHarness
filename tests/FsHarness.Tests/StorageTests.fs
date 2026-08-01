namespace FsHarness.Tests

open System
open System.IO
open System.Threading
open FsHarness.Core
open FsHarness.Infrastructure
open Xunit

module StorageTests =
    let private getResult result =
        match result with
        | Ok value -> value
        | Error error -> failwith $"Unexpected error: {error}"

    let private withTempDirectory action =
        let path = Path.Combine(Path.GetTempPath(), $"fsharness-storage-{Guid.NewGuid():N}")
        Directory.CreateDirectory path |> ignore

        try
            action path
        finally
            Directory.Delete(path, true)

    [<Fact>]
    let ``SQLite journal round trips events and bounded memory`` () =
        withTempDirectory (fun directory ->
            let store = SqliteStore.create (Path.Combine(directory, "harness.db"))
            let journal = SqliteStore.journalPort store
            let runId = RunId.create ()
            let experimentId = ExperimentId.create ()

            journal.Initialize CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            journal.AppendEvent runId (Some experimentId) "Accepted" "12.5" CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let memory =
                { ExperimentId = experimentId
                  Outcome = "Accepted"
                  Metric = Some 12.5M
                  Summary =
                    { Hypothesis = "h"
                      ChangeSummary = "c"
                      ExpectedEffect = "e"
                      ValidationNotes = [ "v" ]
                      ReusableLesson = "l" } }

            journal.SaveMemory runId memory CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let loaded =
                journal.LoadMemories runId 5 6_000 CancellationToken.None
                |> Async.RunSynchronously
                |> getResult

            Assert.Single loaded |> ignore
            Assert.Equal("l", loaded.Head.Summary.ReusableLesson)

            let events = SqliteStore.loadEvents store 20 |> getResult
            Assert.Single events |> ignore
            Assert.Equal("Accepted", events.Head.Kind))

    [<Fact>]
    let ``project lock excludes a second writer`` () =
        withTempDirectory (fun directory ->
            let path = Path.Combine(directory, "run.lock")
            use first = ProjectLock.tryAcquire path |> getResult
            Assert.True(ProjectLock.tryAcquire path |> Result.isError))
