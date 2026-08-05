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
        | Error error ->
            let detail = error.Detail |> Option.defaultValue String.Empty
            failwith $"Unexpected error: {error.Summary} {detail}"

    [<Fact>]
    let ``idle runtime can switch data roots`` () =
        let temporary =
            Path.Combine(Path.GetTempPath(), $"fsharness-root-{Guid.NewGuid():N}")

        let first = Path.Combine(temporary, "first")
        let second = Path.Combine(temporary, "second")

        try
            use runtime = new HarnessRuntime(first, "codex")

            let applied = runtime.TrySetDataRoot second

            match applied with
            | Ok path ->
                Assert.Equal(Path.GetFullPath second, path)
                Assert.Equal(Path.GetFullPath second, runtime.DataRoot)
                Assert.True(Directory.Exists second)
            | Error error -> Assert.Fail error
        finally
            deleteTree temporary

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
    let ``multi agent executor is bounded and returns deterministic order`` () =
        let parent = CommitOid.create (String('a', 40))
        let gate = obj ()
        let mutable active = 0
        let mutable maximum = 0

        let task index =
            let assignment =
                { AgentId = AgentId.create $"agent-{index:D2}"
                  Role = AgentRole.Implementer
                  WorkItemId = WorkItemId.create $"work-{index:D2}"
                  Parent = parent
                  Objective = "fixture" }

            { Assignment = assignment
              Execute =
                fun _ ->
                    async {
                        lock gate (fun () ->
                            active <- active + 1
                            maximum <- max maximum active)

                        do! Async.Sleep 40
                        lock gate (fun () -> active <- active - 1)
                        return Ok index
                    } }

        let results =
            [ 4; 1; 3; 0; 2 ]
            |> List.map task
            |> MultiAgent.run 2 CancellationToken.None
            |> Async.RunSynchronously
            |> getResult

        Assert.Equal(2, maximum)
        Assert.Equal<int list>([ 0; 1; 2; 3; 4 ], results |> List.map (fun result -> result.Result |> getResult))

    [<Fact>]
    let ``plan executor runs independent work in parallel before dependent work`` () =
        let now = DateTimeOffset.UtcNow
        let firstId = WorkItemId.create "01-first"
        let secondId = WorkItemId.create "02-second"
        let finalId = WorkItemId.create "03-final"

        let item id dependencies =
            { Id = id
              Title = WorkItemId.value id
              Objective = "fixture"
              Dependencies = Set.ofList dependencies
              RequiredTools = Set.singleton ToolCapability.ReadRepository
              Budget =
                { MaxRawTokens = 10L
                  MaxDuration = TimeSpan.FromSeconds 5.0
                  MaxAttempts = 1 }
              Priority = 1 }

        let plan =
            { Id = WorkPlanId.create ()
              RunId = RunId.create ()
              ExperimentId = ExperimentId.create ()
              Objective = "fixture"
              Items =
                Map
                    [ firstId, item firstId []
                      secondId, item secondId []
                      finalId, item finalId [ firstId; secondId ] ]
              CreatedAt = now }

        let gate = obj ()
        let mutable active = 0
        let mutable maximum = 0

        let handler _ _ =
            async {
                lock gate (fun () ->
                    active <- active + 1
                    maximum <- max maximum active)

                do! Async.Sleep 40
                lock gate (fun () -> active <- active - 1)
                return Ok(Some TokenUsage.zero)
            }

        let report =
            PlanExecutor.run
                2
                (CommitOid.create (String('a', 40)))
                (Set.singleton ToolCapability.ReadRepository)
                100L
                (now.AddMinutes 1.0)
                handler
                CancellationToken.None
                plan
            |> Async.RunSynchronously
            |> getResult

        Assert.Equal(2, maximum)
        Assert.True(Scheduler.isTerminal report.Schedule)

        Assert.Equal<WorkItemId list>([ firstId; secondId; finalId ], report.Assignments |> List.map _.WorkItemId)

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

            let evaluatorAvailability =
                Path.Combine(temporary, "evaluator-failures-remaining.txt")

            File.WriteAllText(evaluatorAvailability, "1")

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
                      Arguments = [ "--fail-count-file"; evaluatorAvailability ]
                      WorkingDirectory = "."
                      Timeout = TimeSpan.FromSeconds 10.0
                      RequiredConstraints = [ "build"; "tests" ]
                      MaxInconclusiveRetries = 2
                      MaxInfrastructureRetries = 2
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
            Assert.Equal(2M, finalState.ChampionScore)
            Assert.Equal(120L, TokenUsage.rawTotal finalState.Usage)
            Assert.Equal("1", File.ReadAllText(Path.Combine(source, "src", "score.txt")))
            Assert.Equal(sourceHead, runGit source [ "rev-parse"; "HEAD" ])

            let history = runtime.History 100 |> getResult |> List.rev
            Assert.Contains(history, fun event -> event.Kind = "EvaluatorRetryScheduled")
            Assert.Equal("0", File.ReadAllText evaluatorAvailability)
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
            Assert.Equal(Some finalState.Champion, snapshot.Frontier)
            Assert.Equal(Some finalState.Champion, snapshot.Champion)
            Assert.Contains(finalState.Champion, snapshot.ActiveHeads)
            Assert.Single snapshot.Edges |> ignore
            Assert.DoesNotContain(snapshot.Warnings, fun warning -> warning.Contains("private Git repository"))
            Assert.Contains(snapshot.Nodes, fun node -> node.Outcome = EvolutionOutcome.Accepted)

            let graphState =
                let experimentId =
                    match snapshot.Nodes.Head.Id with
                    | ExperimentNode id -> id
                    | BaselineNode -> failwith "Expected an experiment node."

                SqliteStore.create (Path.Combine(dataRoot, "fsharness.db"))
                |> fun store -> SqliteStore.loadExperimentGraphStates store listed.Id
                |> getResult
                |> List.find (fun state -> state.ExperimentId = experimentId)

            Assert.Equal(EvaluationValidity.Valid, graphState.Validity)
            Assert.Equal(ChampionDecision.Promoted, graphState.ChampionDecision)
            Assert.Equal(SearchStatus.ActiveHead, graphState.SearchStatus)

            let children =
                runtime.WorkGraphChildren(listed.Id, inspection.Head, CancellationToken.None)
                |> Async.RunSynchronously
                |> getResult

            Assert.Single children |> ignore
            Assert.Equal(finalState.Champion, children.Head.Commit)

            let leaves =
                runtime.WorkGraphLeaves(listed.Id, CancellationToken.None)
                |> Async.RunSynchronously
                |> getResult

            Assert.Equal<CommitOid list>([ finalState.Champion ], leaves |> List.map _.Commit)

            let lineage =
                runtime.WorkGraphLineage(listed.Id, finalState.Champion, CancellationToken.None)
                |> Async.RunSynchronously
                |> getResult

            Assert.Equal<CommitOid list>([ inspection.Head; finalState.Champion ], lineage |> List.map _.Commit)

            let parents =
                runtime.WorkGraphParents(listed.Id, finalState.Champion, CancellationToken.None)
                |> Async.RunSynchronously
                |> getResult

            Assert.Single parents |> ignore
            Assert.Equal(inspection.Head, parents.Head.Parent)

            let ancestors =
                runtime.WorkGraphAncestors(listed.Id, finalState.Champion, CancellationToken.None)
                |> Async.RunSynchronously
                |> getResult

            Assert.Contains(ancestors, fun node -> node.Commit = inspection.Head)

            let diff =
                runtime.WorkGraphDiff(listed.Id, inspection.Head, finalState.Champion, CancellationToken.None)
                |> Async.RunSynchronously
                |> getResult

            Assert.Contains("+2", diff)

            let knowledge =
                runtime.SearchKnowledge(listed.Id, "primary", 10, CancellationToken.None)
                |> Async.RunSynchronously
                |> getResult

            let metricClaim =
                knowledge |> List.find (fun hit -> hit.Claim.Predicate = "metric:primary")

            Assert.Equal(KnowledgeValue.Number 2M, metricClaim.Claim.Object)
            Assert.Equal(KnowledgeSourceKind.Evaluation, metricClaim.Sources.Head.Kind)

            let repositoryKnowledge =
                runtime.LoadRepositoryKnowledge(listed.Id, CancellationToken.None)
                |> Async.RunSynchronously
                |> getResult

            let repositoryNodes = RepositoryKnowledgeGraph.currentNodes repositoryKnowledge
            Assert.Contains(GraphNodeId.create $"commit:{CommitOid.value finalState.Champion}", repositoryNodes.Keys)
            Assert.Contains(repositoryKnowledge.Edges.Values, fun edge -> edge.Relation = GraphRelationKind.Evaluates)

            let annotation =
                { Id = RunAnnotationId.create ()
                  RunId = listed.Id
                  Target = AnnotationTarget.Experiment children.Head.ExperimentId.Value
                  Author = "integration-test"
                  Body = "Reviewed the accepted deterministic improvement."
                  CreatedAt = DateTimeOffset.UtcNow }

            runtime.AddAnnotation(annotation, CancellationToken.None)
            |> Async.RunSynchronously
            |> getResult
            |> ignore

            let annotations =
                runtime.LoadAnnotations(listed.Id, CancellationToken.None)
                |> Async.RunSynchronously
                |> getResult

            Assert.Equal(annotation, Assert.Single annotations)

            let health =
                runtime.HealthCheck(listed.Id, CancellationToken.None)
                |> Async.RunSynchronously
                |> getResult

            Assert.True(health.Healthy, String.concat " " health.Issues)
            Assert.Equal(0, health.PendingOperationCount)
            Assert.Equal(1, health.WorkPlanCount)
            Assert.True(health.KnowledgeClaimCount >= 2)

            (runtime :> IDisposable).Dispose()
            use recoveredRuntime = new HarnessRuntime(dataRoot, codex)

            let recoveredReport =
                recoveredRuntime.Recover(finalState.Id, CancellationToken.None)
                |> Async.RunSynchronously
                |> getResult

            Assert.Equal(finalState.Id, recoveredReport.RunId)
            let recovered = recoveredRuntime.State |> Option.get
            Assert.Equal(Ready, recovered.Status)
            Assert.Equal(finalState.Champion, recovered.Champion)
            Assert.Equal(1, recovered.Attempted)
            Assert.Equal(1, recovered.AcceptedCount)
            Assert.Equal(120L, TokenUsage.rawTotal recovered.Usage)

            recoveredRuntime.Start() |> getResult |> ignore

            let stoppedAtRestoredBudget =
                SpinWait.SpinUntil(
                    (fun () ->
                        recoveredRuntime.State
                        |> Option.exists (fun state -> state.Status = Completed "Experiment budget exhausted.")),
                    TimeSpan.FromSeconds 5.0
                )

            Assert.True(stoppedAtRestoredBudget)
            Assert.Equal(1, recoveredRuntime.State.Value.Attempted)
        finally
            deleteTree temporary
