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

    let private config =
        { SchemaVersion = HarnessConfig.currentSchemaVersion
          SourcePath = "/tmp/source"
          BaseCommit = CommitOid.create (String('a', 40))
          Objective = "Fixture"
          EditablePaths = [ "src/**" ]
          SeedPatches = []
          Evaluator =
            { Executable = "fake"
              Arguments = []
              WorkingDirectory = "."
              Timeout = TimeSpan.FromSeconds 5.0
              RequiredConstraints = []
              MaxInconclusiveRetries = 0 }
          Metric =
            { Name = "primary"
              Direction = Maximize
              MinDelta = 0M
              Target = None
              Comparison = RetainedScore }
          Model = Defaults.model
          PromptProfile = Defaults.promptProfile
          Budgets = Defaults.budgets
          PromotionMode = AutoWhenStrictlyBetter }

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

    [<Fact>]
    let ``experiment lineage lifecycle round trips parent candidate and outcome`` () =
        withTempDirectory (fun directory ->
            let store = SqliteStore.create (Path.Combine(directory, "harness.db"))

            SqliteStore.initialize store CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let runId = RunId.create ()
            let experimentId = ExperimentId.create ()
            let parent = CommitOid.create (String('a', 40))
            let candidate = CommitOid.create (String('b', 40))

            SqliteStore.beginExperiment store runId experimentId 1 (Some parent) "Active" CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            SqliteStore.updateExperimentCandidate store experimentId candidate CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            SqliteStore.completeExperiment store experimentId "Accepted" CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let record = SqliteStore.loadExperiments store runId |> getResult |> Assert.Single
            Assert.Equal(parent, record.Parent.Value)
            Assert.Equal(candidate, record.Candidate.Value)
            Assert.Equal("Accepted", record.Outcome))

    [<Fact>]
    let ``persisted run appears in evolution run listing`` () =
        withTempDirectory (fun directory ->
            let store = SqliteStore.create (Path.Combine(directory, "harness.db"))

            SqliteStore.initialize store CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let runId = RunId.create ()

            SqliteStore.saveRun store runId Fixtures.config "Ready" CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let runs = SqliteStore.loadRuns store |> getResult
            Assert.Single runs |> ignore)
