namespace FsHarness.Tests

open System
open System.IO
open System.Threading
open FsHarness.Core
open FsHarness.Infrastructure
open Microsoft.Data.Sqlite
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
              MaxInconclusiveRetries = 0
              MaxInfrastructureRetries = 0
              InfrastructureRetryDelay = TimeSpan.FromMilliseconds 10.0 }
          Metric =
            { Name = "primary"
              Direction = Maximize
              MinDelta = 0M
              Target = None
              Comparison = RetainedScore }
          Model = Defaults.model
          PromptProfile = Defaults.promptProfile
          GraphSearch = Defaults.graphSearch
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

            let usage =
                { InputTokens = 1_000L
                  CachedInputTokens = 700L
                  OutputTokens = 200L
                  ReasoningOutputTokens = 50L }

            journal.SaveUsage runId experimentId (Some usage) CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let memory =
                { ExperimentId = experimentId
                  Outcome = "Accepted"
                  Metric = Some 12.5M
                  Summary =
                    { HypothesisFamily = "fixture"
                      Hypothesis = "h"
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
            Assert.Equal("Accepted", events.Head.Kind)
            Assert.Equal(Some usage, events.Head.Usage))

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

    [<Fact>]
    let ``persisted paired metric configuration can be parsed for recovery`` () =
        withTempDirectory (fun directory ->
            let store = SqliteStore.create (Path.Combine(directory, "harness.db"))

            SqliteStore.initialize store CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let runId = RunId.create ()

            let expected =
                { config with
                    SourcePath = Path.GetFullPath config.SourcePath
                    Metric =
                        { config.Metric with
                            Direction = Minimize
                            Target = Some 4.5M
                            Comparison = EvaluationMetric "frontier_metric" }
                    PromotionMode = ReviewStrictWinners }

            SqliteStore.saveRun store runId expected "Ready" CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let stored = SqliteStore.loadRuns store |> getResult |> Assert.Single

            let json =
                stored.ConfigJson
                |> Option.defaultWith (fun () -> failwith "Missing configuration")

            match ConfigFile.parse json with
            | Error errors -> Assert.Fail(String.concat " " errors)
            | Ok actual -> Assert.Equal(expected, actual))

    [<Fact>]
    let ``durable promotion operation and artifact integrity round trip`` () =
        withTempDirectory (fun directory ->
            let store = SqliteStore.create (Path.Combine(directory, "harness.db"))

            SqliteStore.initialize store CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let runId = RunId.create ()
            let experimentId = ExperimentId.create ()
            let artifactPath = Path.Combine(directory, "evaluation.json")
            File.WriteAllText(artifactPath, "{\"score\":2}")

            SqliteStore.saveRun store runId config "Running" CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            SqliteStore.beginExperiment
                store
                runId
                experimentId
                1
                (Some config.BaseCommit)
                "Active"
                CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let workPlan =
                WorkPlan.standardExperiment
                    runId
                    experimentId
                    config.Objective
                    config.Budgets
                    config.Evaluator.Timeout
                    DateTimeOffset.UtcNow

            SqliteStore.saveWorkPlan store workPlan CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let storedPlan = SqliteStore.loadWorkPlans store runId |> getResult |> Assert.Single
            Assert.Equal(workPlan.Id, storedPlan.Id)
            Assert.Contains("generate-with-codex", storedPlan.PlanJson)

            SqliteStore.updateWorkPlanStatus store workPlan.Id "finished" CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            SqliteStore.beginDurableOperation
                store
                runId
                experimentId
                "advance-frontier"
                "{\"candidate\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}"
                CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            SqliteStore.saveArtifact store runId (Some experimentId) "evaluation" artifactPath CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let pending =
                SqliteStore.loadPendingOperations store runId |> getResult |> Assert.Single

            Assert.Equal("advance-frontier", pending.Kind)

            let artifact =
                SqliteStore.loadArtifactsForRun store runId |> getResult |> Assert.Single

            SqliteStore.verifyArtifact artifact |> getResult |> ignore
            File.AppendAllText(artifactPath, "changed")
            Assert.True(SqliteStore.verifyArtifact artifact |> Result.isError)

            SqliteStore.completeDurableOperation
                store
                runId
                experimentId
                "advance-frontier"
                "completed"
                CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            Assert.Empty(SqliteStore.loadPendingOperations store runId |> getResult))

    [<Fact>]
    let ``provenance knowledge graph round trips entities aliases sources and claims`` () =
        withTempDirectory (fun directory ->
            let store = SqliteStore.create (Path.Combine(directory, "harness.db"))

            SqliteStore.initialize store CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let runId = RunId.create ()

            SqliteStore.saveRun store runId config "Ready" CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let entity =
                { Id = KnowledgeEntityId.create ()
                  Kind = KnowledgeEntityKind.File
                  CanonicalName = "Planning.fs"
                  Attributes = Map [ "layer", "core" ] }

            let source =
                { Id = KnowledgeSourceId.create ()
                  RunId = runId
                  ExperimentId = None
                  Kind = KnowledgeSourceKind.Artifact
                  Location = Path.Combine(directory, "evaluation.json")
                  Sha256 = Some(String('a', 64))
                  CapturedAt = DateTimeOffset.UtcNow }

            let claim =
                { Id = KnowledgeClaimId.create ()
                  RunId = runId
                  Subject = entity.Id
                  Predicate = "contains"
                  Object = KnowledgeValue.Text "budget-aware scheduler"
                  Confidence = 0.9M
                  Sources = Set.singleton source.Id
                  Supersedes = None
                  CreatedAt = DateTimeOffset.UtcNow }

            SqliteStore.saveKnowledgeEntity store runId entity CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            SqliteStore.saveKnowledgeAlias store runId entity.Id "planner" CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            SqliteStore.saveKnowledgeSource store source CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            SqliteStore.saveKnowledgeClaim store claim CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let graph = SqliteStore.loadKnowledgeGraph store runId |> getResult
            Assert.Equal(entity, graph.Entities[entity.Id])
            Assert.Contains(entity.Id, graph.Aliases["planner"])
            let hit = KnowledgeGraph.search "planner scheduler" 5 graph |> Assert.Single
            Assert.Equal(claim.Id, hit.Claim.Id)
            Assert.Equal(source.Id, hit.Sources.Head.Id))

    [<Fact>]
    let ``repository graph updates are atomic idempotent and project isolated`` () =
        withTempDirectory (fun directory ->
            let store = SqliteStore.create (Path.Combine(directory, "harness.db"))

            SqliteStore.initialize store CancellationToken.None
            |> Async.RunSynchronously
            |> getResult

            let firstRun = RunId.create ()
            let secondRun = RunId.create ()

            let firstConfig =
                { config with
                    SourcePath = Path.Combine(directory, "repo-a") }

            let secondConfig =
                { config with
                    SourcePath = Path.Combine(directory, "repo-b") }

            SqliteStore.saveRun store firstRun firstConfig "Ready" CancellationToken.None
            |> Async.RunSynchronously
            |> getResult

            SqliteStore.saveRun store secondRun secondConfig "Ready" CancellationToken.None
            |> Async.RunSynchronously
            |> getResult

            let update runId projectId name =
                { ProjectId = projectId
                  RunId = runId
                  AgentId = "test"
                  IdempotencyKey = $"update:{RunId.text runId}"
                  Nodes =
                    [ { Id = GraphNodeId.create "shared-id"
                        Kind = GraphNodeKind.Entity
                        CanonicalName = name
                        Attributes = Map.empty
                        Version = 1
                        OriginRunId = runId
                        CreatedAt = DateTimeOffset.UtcNow } ]
                  Edges = [] }

            let firstProject = SqliteStore.projectIdForRun store firstRun |> getResult
            let secondProject = SqliteStore.projectIdForRun store secondRun |> getResult
            let firstUpdate = update firstRun firstProject "repository A"

            let inserted =
                SqliteStore.saveRepositoryGraphUpdate store firstUpdate CancellationToken.None
                |> Async.RunSynchronously
                |> getResult

            let duplicate =
                SqliteStore.saveRepositoryGraphUpdate store firstUpdate CancellationToken.None
                |> Async.RunSynchronously
                |> getResult

            SqliteStore.saveRepositoryGraphUpdate
                store
                (update secondRun secondProject "repository B")
                CancellationToken.None
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            Assert.True inserted
            Assert.False duplicate

            let firstGraph =
                SqliteStore.loadRepositoryKnowledgeGraph store firstProject |> getResult

            let secondGraph =
                SqliteStore.loadRepositoryKnowledgeGraph store secondProject |> getResult

            let firstNodes = RepositoryKnowledgeGraph.currentNodes firstGraph
            let secondNodes = RepositoryKnowledgeGraph.currentNodes secondGraph
            Assert.Equal("repository A", firstNodes[GraphNodeId.create "shared-id"].CanonicalName)
            Assert.Equal("repository B", secondNodes[GraphNodeId.create "shared-id"].CanonicalName))

    [<Fact>]
    let ``v6 migration backs up and reconstructs edges champion and active heads`` () =
        withTempDirectory (fun directory ->
            let databasePath = Path.Combine(directory, "legacy.db")
            let runId = RunId.create ()
            let projectId = "legacy-project"
            let baseline = String('a', 40)
            let champion = String('b', 40)
            let retained = String('c', 40)
            let configPath = Path.Combine(directory, "config.json")

            ConfigFile.write
                configPath
                { config with
                    BaseCommit = CommitOid.create baseline }
            |> getResult
            |> ignore

            let configJson = File.ReadAllText configPath
            use database = new SqliteConnection($"Data Source={databasePath}")
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                """
                PRAGMA user_version = 5;
                CREATE TABLE projects(id TEXT PRIMARY KEY, source_path TEXT NOT NULL, created_at TEXT NOT NULL);
                CREATE TABLE runs(id TEXT PRIMARY KEY, project_id TEXT, config_json TEXT, status TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE experiments(id TEXT PRIMARY KEY, run_id TEXT NOT NULL, sequence INTEGER, parent_oid TEXT, candidate_oid TEXT, outcome TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE evaluations(id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL, experiment_id TEXT, result_json TEXT NOT NULL, created_at TEXT NOT NULL);
                INSERT INTO projects VALUES ($project, '/legacy', $now);
                INSERT INTO runs VALUES ($run, $project, $config, 'Interrupted', $now, $now);
                INSERT INTO experiments VALUES ($accepted, $run, 1, $baseline, $champion, 'Accepted', $now, $now);
                INSERT INTO experiments VALUES ($retainedId, $run, 2, $baseline, $retained, 'Rejected: Not strictly better', $now, $now);
                """

            let parameter name (value: obj) =
                command.Parameters.AddWithValue(name, value) |> ignore

            parameter "$project" projectId
            parameter "$run" (RunId.text runId)
            parameter "$config" configJson
            parameter "$now" (DateTimeOffset.UtcNow.ToString("o"))
            parameter "$accepted" (ExperimentId.text (ExperimentId.create ()))
            parameter "$retainedId" (ExperimentId.text (ExperimentId.create ()))
            parameter "$baseline" baseline
            parameter "$champion" champion
            parameter "$retained" retained
            command.ExecuteNonQuery() |> ignore
            database.Close()

            let store = SqliteStore.create databasePath

            SqliteStore.initialize store CancellationToken.None
            |> Async.RunSynchronously
            |> getResult

            Assert.True(File.Exists(databasePath + ".pre-v6.bak"))
            let edges = SqliteStore.loadExperimentEdges store runId |> getResult
            Assert.Equal(2, edges.Length)

            let heads =
                SqliteStore.loadActiveHeads store runId
                |> getResult
                |> List.map _.Commit
                |> Set.ofList

            Assert.Contains(CommitOid.create champion, heads)
            Assert.Contains(CommitOid.create retained, heads)

            use migrated = new SqliteConnection($"Data Source={databasePath}")
            migrated.Open()
            use version = migrated.CreateCommand()
            version.CommandText <- "PRAGMA user_version;"
            Assert.Equal(6L, version.ExecuteScalar() |> Convert.ToInt64))
