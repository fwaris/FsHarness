namespace FsHarness.Infrastructure

open System
open System.Data
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open FsHarness.Core
open Microsoft.Data.Sqlite

type HistoryEvent =
    { Sequence: int64
      RunId: RunId
      ExperimentId: ExperimentId option
      Kind: string
      Payload: string
      CreatedAt: DateTimeOffset }

type StoredRun =
    { Id: RunId
      SourcePath: string
      Status: string
      ConfigJson: string option
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset }

type StoredExperiment =
    { Id: ExperimentId
      RunId: RunId
      Sequence: int
      Parent: CommitOid option
      Candidate: CommitOid option
      Outcome: string
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset }

type StoredEvaluation =
    { ExperimentId: ExperimentId
      ResultJson: string
      CreatedAt: DateTimeOffset }

type StoredUsage =
    { ExperimentId: ExperimentId
      Usage: TokenUsage option }

type StoredArtifact =
    { Id: int64
      RunId: RunId
      ExperimentId: ExperimentId option
      Kind: string
      Path: string
      Sha256: string option
      CreatedAt: DateTimeOffset }

type DurableOperation =
    { RunId: RunId
      ExperimentId: ExperimentId
      Kind: string
      Status: string
      Payload: string
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset }

type StoredWorkPlan =
    { Id: WorkPlanId
      RunId: RunId
      ExperimentId: ExperimentId
      Objective: string
      PlanJson: string
      Status: string
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset }

type SqliteStore = private { DatabasePath: string }

[<RequireQualifiedAccess>]
module SqliteStore =
    let create databasePath =
        { DatabasePath = Path.GetFullPath databasePath }

    let private connection store =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- store.DatabasePath
        builder.Mode <- SqliteOpenMode.ReadWriteCreate
        builder.Cache <- SqliteCacheMode.Shared
        builder.Pooling <- false
        new SqliteConnection(builder.ToString())

    let private persistenceError code summary (exceptionValue: exn) =
        HarnessError.create code HarnessErrorCategory.Persistence summary
        |> HarnessError.withDetail exceptionValue.Message

    let initialize store (_: CancellationToken) =
        async {
            try
                Directory.CreateDirectory(Path.GetDirectoryName store.DatabasePath) |> ignore
                use database = connection store
                database.Open()

                use command = database.CreateCommand()

                command.CommandText <-
                    """
                    PRAGMA journal_mode = WAL;
                    PRAGMA foreign_keys = ON;
                    BEGIN IMMEDIATE;
                    CREATE TABLE IF NOT EXISTS schema_info (
                        version INTEGER NOT NULL
                    );
                    INSERT INTO schema_info(version)
                    SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_info);
                    CREATE TABLE IF NOT EXISTS schema_migrations (
                        version INTEGER PRIMARY KEY,
                        applied_at TEXT NOT NULL
                    );
                    INSERT OR IGNORE INTO schema_migrations(version, applied_at)
                    VALUES (1, CURRENT_TIMESTAMP);
                    CREATE TABLE IF NOT EXISTS projects (
                        id TEXT PRIMARY KEY,
                        source_path TEXT NOT NULL,
                        created_at TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS runs (
                        id TEXT PRIMARY KEY,
                        project_id TEXT,
                        config_json TEXT,
                        status TEXT,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS experiments (
                        id TEXT PRIMARY KEY,
                        run_id TEXT NOT NULL,
                        sequence INTEGER,
                        parent_oid TEXT,
                        candidate_oid TEXT,
                        outcome TEXT,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS events (
                        sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                        run_id TEXT NOT NULL,
                        experiment_id TEXT,
                        kind TEXT NOT NULL,
                        payload TEXT NOT NULL,
                        created_at TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS events_run_sequence ON events(run_id, sequence);
                    CREATE INDEX IF NOT EXISTS experiments_run_sequence ON experiments(run_id, sequence);
                    CREATE TABLE IF NOT EXISTS evaluations (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        run_id TEXT NOT NULL,
                        experiment_id TEXT,
                        result_json TEXT NOT NULL,
                        created_at TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS usage (
                        run_id TEXT NOT NULL,
                        experiment_id TEXT NOT NULL,
                        input_tokens INTEGER,
                        cached_input_tokens INTEGER,
                        output_tokens INTEGER,
                        reasoning_output_tokens INTEGER,
                        PRIMARY KEY(run_id, experiment_id)
                    );
                    CREATE TABLE IF NOT EXISTS memories (
                        run_id TEXT NOT NULL,
                        experiment_id TEXT NOT NULL,
                        outcome TEXT NOT NULL,
                        metric TEXT,
                        hypothesis TEXT NOT NULL,
                        change_summary TEXT NOT NULL,
                        expected_effect TEXT NOT NULL,
                        validation_notes_json TEXT NOT NULL,
                        reusable_lesson TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        PRIMARY KEY(run_id, experiment_id)
                    );
                    CREATE TABLE IF NOT EXISTS artifacts (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        run_id TEXT NOT NULL,
                        experiment_id TEXT,
                        kind TEXT NOT NULL,
                        path TEXT NOT NULL,
                        sha256 TEXT,
                        created_at TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS artifacts_run_experiment
                        ON artifacts(run_id, experiment_id, created_at);
                    CREATE TABLE IF NOT EXISTS durable_operations (
                        run_id TEXT NOT NULL,
                        experiment_id TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        status TEXT NOT NULL,
                        payload TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        PRIMARY KEY(run_id, experiment_id, kind),
                        FOREIGN KEY(run_id) REFERENCES runs(id),
                        FOREIGN KEY(experiment_id) REFERENCES experiments(id)
                    );
                    CREATE INDEX IF NOT EXISTS durable_operations_status
                        ON durable_operations(run_id, status, updated_at);
                    CREATE TABLE IF NOT EXISTS reproducibility_manifests (
                        run_id TEXT PRIMARY KEY,
                        manifest_json TEXT NOT NULL,
                        sha256 TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        FOREIGN KEY(run_id) REFERENCES runs(id)
                    );
                    CREATE TABLE IF NOT EXISTS work_plans (
                        id TEXT PRIMARY KEY,
                        run_id TEXT NOT NULL,
                        experiment_id TEXT NOT NULL UNIQUE,
                        objective TEXT NOT NULL,
                        plan_json TEXT NOT NULL,
                        status TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        FOREIGN KEY(run_id) REFERENCES runs(id),
                        FOREIGN KEY(experiment_id) REFERENCES experiments(id)
                    );
                    CREATE INDEX IF NOT EXISTS work_plans_run_status
                        ON work_plans(run_id, status, created_at);
                    CREATE TABLE IF NOT EXISTS knowledge_entities (
                        run_id TEXT NOT NULL,
                        id TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        canonical_name TEXT NOT NULL,
                        attributes_json TEXT NOT NULL,
                        PRIMARY KEY(run_id, id),
                        FOREIGN KEY(run_id) REFERENCES runs(id)
                    );
                    CREATE TABLE IF NOT EXISTS knowledge_aliases (
                        run_id TEXT NOT NULL,
                        entity_id TEXT NOT NULL,
                        alias TEXT NOT NULL,
                        normalized_alias TEXT NOT NULL,
                        PRIMARY KEY(run_id, entity_id, normalized_alias),
                        FOREIGN KEY(run_id, entity_id) REFERENCES knowledge_entities(run_id, id)
                    );
                    CREATE INDEX IF NOT EXISTS knowledge_alias_lookup
                        ON knowledge_aliases(run_id, normalized_alias);
                    CREATE TABLE IF NOT EXISTS knowledge_sources (
                        id TEXT PRIMARY KEY,
                        run_id TEXT NOT NULL,
                        experiment_id TEXT,
                        kind TEXT NOT NULL,
                        location TEXT NOT NULL,
                        sha256 TEXT,
                        captured_at TEXT NOT NULL,
                        FOREIGN KEY(run_id) REFERENCES runs(id)
                    );
                    CREATE TABLE IF NOT EXISTS knowledge_claims (
                        id TEXT PRIMARY KEY,
                        run_id TEXT NOT NULL,
                        subject_id TEXT NOT NULL,
                        predicate TEXT NOT NULL,
                        object_kind TEXT NOT NULL,
                        object_value TEXT NOT NULL,
                        confidence TEXT NOT NULL,
                        supersedes_id TEXT,
                        created_at TEXT NOT NULL,
                        FOREIGN KEY(run_id, subject_id) REFERENCES knowledge_entities(run_id, id),
                        FOREIGN KEY(supersedes_id) REFERENCES knowledge_claims(id)
                    );
                    CREATE INDEX IF NOT EXISTS knowledge_claim_lookup
                        ON knowledge_claims(run_id, subject_id, predicate);
                    CREATE TABLE IF NOT EXISTS knowledge_claim_sources (
                        claim_id TEXT NOT NULL,
                        source_id TEXT NOT NULL,
                        PRIMARY KEY(claim_id, source_id),
                        FOREIGN KEY(claim_id) REFERENCES knowledge_claims(id),
                        FOREIGN KEY(source_id) REFERENCES knowledge_sources(id)
                    );
                    CREATE TABLE IF NOT EXISTS run_annotations (
                        id TEXT PRIMARY KEY,
                        run_id TEXT NOT NULL,
                        target_kind TEXT NOT NULL,
                        target_id TEXT,
                        author TEXT NOT NULL,
                        body TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        FOREIGN KEY(run_id) REFERENCES runs(id)
                    );
                    CREATE INDEX IF NOT EXISTS run_annotations_timeline
                        ON run_annotations(run_id, created_at);
                    INSERT OR IGNORE INTO schema_migrations(version, applied_at)
                    VALUES (2, CURRENT_TIMESTAMP);
                    INSERT OR IGNORE INTO schema_migrations(version, applied_at)
                    VALUES (3, CURRENT_TIMESTAMP);
                    INSERT OR IGNORE INTO schema_migrations(version, applied_at)
                    VALUES (4, CURRENT_TIMESTAMP);
                    INSERT OR IGNORE INTO schema_migrations(version, applied_at)
                    VALUES (5, CURRENT_TIMESTAMP);
                    UPDATE schema_info SET version = 5;
                    PRAGMA user_version = 5;
                    COMMIT;
                    """

                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.initialize_failed"
                            "Could not initialize the run journal."
                            exceptionValue
                    )
        }

    let private addParameter (command: SqliteCommand) name value =
        let parameter = command.CreateParameter()
        parameter.ParameterName <- name
        parameter.Value <- value
        command.Parameters.Add parameter |> ignore

    let private serializeConfig (config: HarnessConfig) =
        let direction =
            match config.Metric.Direction with
            | Maximize -> "maximize"
            | Minimize -> "minimize"

        let comparison: obj =
            match config.Metric.Comparison with
            | RetainedScore -> box "retainedScore"
            | EvaluationMetric name -> box {| evaluationMetric = name |}

        let promotionMode =
            match config.PromotionMode with
            | AutoWhenStrictlyBetter -> "auto"
            | ReviewStrictWinners -> "review"

        JsonSerializer.Serialize
            {| schemaVersion = config.SchemaVersion
               sourcePath = Path.GetFullPath config.SourcePath
               baseCommit = CommitOid.value config.BaseCommit
               objective = config.Objective
               editablePaths = List.toArray config.EditablePaths
               seedPatches = List.toArray config.SeedPatches
               evaluator =
                {| executable = config.Evaluator.Executable
                   arguments = List.toArray config.Evaluator.Arguments
                   workingDirectory = config.Evaluator.WorkingDirectory
                   timeoutSeconds = config.Evaluator.Timeout.TotalSeconds
                   requiredConstraints = List.toArray config.Evaluator.RequiredConstraints
                   maxInconclusiveRetries = config.Evaluator.MaxInconclusiveRetries |}
               metric =
                {| name = config.Metric.Name
                   direction = direction
                   minDelta = config.Metric.MinDelta
                   target = config.Metric.Target |> Option.map box |> Option.defaultValue null
                   comparison = comparison |}
               model =
                {| id = config.Model.Id
                   reasoningEffort = ReasoningEffort.toConfigValue config.Model.Effort |}
               promptProfile =
                {| maxMemoryCount = config.PromptProfile.MaxMemoryCount
                   maxMemoryCharacters = config.PromptProfile.MaxMemoryCharacters
                   maxEvaluationFindings = config.PromptProfile.MaxEvaluationFindings
                   maxEvaluationCharacters = config.PromptProfile.MaxEvaluationCharacters |}
               budgets =
                {| maxExperiments = config.Budgets.MaxExperiments
                   maxRawTokens = config.Budgets.MaxRawTokens
                   maxDurationSeconds = config.Budgets.MaxDuration.TotalSeconds
                   codexTimeoutSeconds = config.Budgets.CodexTimeout.TotalSeconds
                   maxConsecutiveNonImprovements = config.Budgets.MaxConsecutiveNonImprovements
                   maxConsecutiveFailures = config.Budgets.MaxConsecutiveFailures |}
               promotionMode = promotionMode |}

    let saveRun store runId (config: HarnessConfig) status (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use transaction = database.BeginTransaction()
                let timestamp = DateTimeOffset.UtcNow.ToString("o")
                let projectId = Path.GetFullPath config.SourcePath

                use projectCommand = database.CreateCommand()
                projectCommand.Transaction <- transaction

                projectCommand.CommandText <-
                    "INSERT INTO projects(id, source_path, created_at) VALUES ($id, $path, $created) ON CONFLICT(id) DO NOTHING;"

                addParameter projectCommand "$id" projectId
                addParameter projectCommand "$path" projectId
                addParameter projectCommand "$created" timestamp
                projectCommand.ExecuteNonQuery() |> ignore

                use runCommand = database.CreateCommand()
                runCommand.Transaction <- transaction

                runCommand.CommandText <-
                    "INSERT INTO runs(id, project_id, config_json, status, created_at, updated_at) VALUES ($id, $project, $config, $status, $created, $updated);"

                addParameter runCommand "$id" (RunId.text runId)
                addParameter runCommand "$project" projectId
                addParameter runCommand "$config" (serializeConfig config)
                addParameter runCommand "$status" status
                addParameter runCommand "$created" timestamp
                addParameter runCommand "$updated" timestamp
                runCommand.ExecuteNonQuery() |> ignore
                transaction.Commit()
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.run_failed"
                            "Could not persist immutable run configuration."
                            exceptionValue
                    )
        }

    let beginExperiment store runId experimentId sequence parent outcome (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    """
                    INSERT INTO experiments(id, run_id, sequence, parent_oid, candidate_oid, outcome, created_at, updated_at)
                    VALUES ($id, $run, $sequence, $parent, NULL, $outcome, $created, $updated)
                    ON CONFLICT(id) DO UPDATE SET
                        run_id = excluded.run_id,
                        sequence = excluded.sequence,
                        parent_oid = excluded.parent_oid,
                        outcome = excluded.outcome,
                        updated_at = excluded.updated_at;
                    """

                let timestamp = DateTimeOffset.UtcNow.ToString("o")
                addParameter command "$id" (ExperimentId.text experimentId)
                addParameter command "$run" (RunId.text runId)
                addParameter command "$sequence" sequence

                addParameter
                    command
                    "$parent"
                    (parent
                     |> Option.map CommitOid.value
                     |> Option.map box
                     |> Option.defaultValue DBNull.Value)

                addParameter command "$outcome" outcome
                addParameter command "$created" timestamp
                addParameter command "$updated" timestamp
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.experiment_begin_failed"
                            "Could not persist the planned experiment."
                            exceptionValue
                    )
        }

    let updateExperimentCandidate store experimentId candidate (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    "UPDATE experiments SET candidate_oid = $candidate, updated_at = $updated WHERE id = $id;"

                addParameter command "$id" (ExperimentId.text experimentId)
                addParameter command "$candidate" (CommitOid.value candidate)
                addParameter command "$updated" (DateTimeOffset.UtcNow.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.experiment_candidate_failed"
                            "Could not persist the captured candidate."
                            exceptionValue
                    )
        }

    let completeExperiment store experimentId outcome (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    "UPDATE experiments SET outcome = $outcome, updated_at = $updated WHERE id = $id;"

                addParameter command "$id" (ExperimentId.text experimentId)
                addParameter command "$outcome" outcome
                addParameter command "$updated" (DateTimeOffset.UtcNow.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.experiment_complete_failed"
                            "Could not persist the experiment outcome."
                            exceptionValue
                    )
        }

    let updateRunStatus store runId status (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()
                command.CommandText <- "UPDATE runs SET status = $status, updated_at = $updated WHERE id = $id;"
                addParameter command "$id" (RunId.text runId)
                addParameter command "$status" status
                addParameter command "$updated" (DateTimeOffset.UtcNow.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return Error(persistenceError "sqlite.run_status_failed" "Could not update run status." exceptionValue)
        }

    let saveEvaluation store runId experimentId (evaluation: EvaluationResult) (_: CancellationToken) =
        async {
            try
                let resultJson =
                    JsonSerializer.Serialize
                        {| schemaVersion = evaluation.SchemaVersion
                           status =
                            match evaluation.Status with
                            | EvaluationStatus.Complete -> "complete"
                            | EvaluationStatus.Inconclusive -> "inconclusive"
                           constraints = evaluation.Constraints |> Map.toArray |> dict
                           metrics = evaluation.Metrics |> Map.toArray |> dict
                           summary = evaluation.Summary
                           evidence = List.toArray evaluation.Evidence |}

                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    "INSERT INTO evaluations(run_id, experiment_id, result_json, created_at) VALUES ($run, $experiment, $result, $created);"

                addParameter command "$run" (RunId.text runId)
                addParameter command "$experiment" (ExperimentId.text experimentId)
                addParameter command "$result" resultJson
                addParameter command "$created" (DateTimeOffset.UtcNow.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.evaluation_failed"
                            "Could not persist evaluation result."
                            exceptionValue
                    )
        }

    let saveArtifact store runId experimentId kind path (_: CancellationToken) =
        async {
            try
                let hash =
                    if File.Exists path then
                        AtomicFile.sha256 path |> box
                    else
                        DBNull.Value

                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    "INSERT INTO artifacts(run_id, experiment_id, kind, path, sha256, created_at) VALUES ($run, $experiment, $kind, $path, $hash, $created);"

                addParameter command "$run" (RunId.text runId)

                addParameter
                    command
                    "$experiment"
                    (experimentId
                     |> Option.map ExperimentId.text
                     |> Option.map box
                     |> Option.defaultValue DBNull.Value)

                addParameter command "$kind" kind
                addParameter command "$path" (Path.GetFullPath path)
                addParameter command "$hash" hash
                addParameter command "$created" (DateTimeOffset.UtcNow.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError "sqlite.artifact_failed" "Could not persist artifact metadata." exceptionValue
                    )
        }

    let beginDurableOperation store runId experimentId kind payload (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()
                let timestamp = DateTimeOffset.UtcNow.ToString("o")

                command.CommandText <-
                    """
                    INSERT INTO durable_operations(
                        run_id, experiment_id, kind, status, payload, created_at, updated_at)
                    VALUES ($run, $experiment, $kind, 'pending', $payload, $created, $updated)
                    ON CONFLICT(run_id, experiment_id, kind) DO UPDATE SET
                        status = 'pending',
                        payload = excluded.payload,
                        updated_at = excluded.updated_at;
                    """

                addParameter command "$run" (RunId.text runId)
                addParameter command "$experiment" (ExperimentId.text experimentId)
                addParameter command "$kind" kind
                addParameter command "$payload" payload
                addParameter command "$created" timestamp
                addParameter command "$updated" timestamp
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.operation_begin_failed"
                            "Could not persist the durable operation intent."
                            exceptionValue
                    )
        }

    let completeDurableOperation store runId experimentId kind status (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    """
                    UPDATE durable_operations
                    SET status = $status, updated_at = $updated
                    WHERE run_id = $run AND experiment_id = $experiment AND kind = $kind;
                    """

                addParameter command "$run" (RunId.text runId)
                addParameter command "$experiment" (ExperimentId.text experimentId)
                addParameter command "$kind" kind
                addParameter command "$status" status
                addParameter command "$updated" (DateTimeOffset.UtcNow.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.operation_complete_failed"
                            "Could not update the durable operation."
                            exceptionValue
                    )
        }

    let saveReproducibilityManifest store runId (manifestJson: string) (_: CancellationToken) =
        async {
            try
                let manifestBytes: byte array = Encoding.UTF8.GetBytes manifestJson

                let sha256: string =
                    SHA256.HashData manifestBytes
                    |> Convert.ToHexString
                    |> fun value -> value.ToLowerInvariant()

                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    """
                    INSERT INTO reproducibility_manifests(run_id, manifest_json, sha256, created_at)
                    VALUES ($run, $manifest, $sha256, $created)
                    ON CONFLICT(run_id) DO UPDATE SET
                        manifest_json = excluded.manifest_json,
                        sha256 = excluded.sha256;
                    """

                addParameter command "$run" (RunId.text runId)
                addParameter command "$manifest" manifestJson
                addParameter command "$sha256" sha256
                addParameter command "$created" (DateTimeOffset.UtcNow.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok sha256
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.manifest_save_failed"
                            "Could not persist the reproducibility manifest."
                            exceptionValue
                    )
        }

    let private toolCapabilityText capability =
        match capability with
        | ToolCapability.ReadRepository -> "read-repository"
        | ToolCapability.EditRepository -> "edit-repository"
        | ToolCapability.GenerateWithCodex -> "generate-with-codex"
        | ToolCapability.SnapshotWithGit -> "snapshot-with-git"
        | ToolCapability.RunDeterministicEvaluator -> "run-deterministic-evaluator"
        | ToolCapability.PromoteFrontier -> "promote-frontier"
        | ToolCapability.QueryKnowledge -> "query-knowledge"

    let private serializeWorkPlan (plan: WorkPlan) =
        let items =
            plan.Items
            |> Map.values
            |> Seq.sortBy (fun item -> -item.Priority, WorkItemId.value item.Id)
            |> Seq.map (fun item ->
                {| id = WorkItemId.value item.Id
                   title = item.Title
                   objective = item.Objective
                   dependencies = item.Dependencies |> Seq.map WorkItemId.value |> Seq.toArray
                   requiredTools = item.RequiredTools |> Seq.map toolCapabilityText |> Seq.toArray
                   maxRawTokens = item.Budget.MaxRawTokens
                   maxDurationSeconds = item.Budget.MaxDuration.TotalSeconds
                   maxAttempts = item.Budget.MaxAttempts
                   priority = item.Priority |})
            |> Seq.toArray

        JsonSerializer.Serialize
            {| schemaVersion = 1
               id = WorkPlanId.text plan.Id
               runId = RunId.text plan.RunId
               experimentId = ExperimentId.text plan.ExperimentId
               objective = plan.Objective
               createdAt = plan.CreatedAt
               items = items |}

    let saveWorkPlan store (plan: WorkPlan) (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()
                let timestamp = DateTimeOffset.UtcNow.ToString("o")

                command.CommandText <-
                    """
                    INSERT INTO work_plans(
                        id, run_id, experiment_id, objective, plan_json, status, created_at, updated_at)
                    VALUES ($id, $run, $experiment, $objective, $plan, 'pending', $created, $updated)
                    ON CONFLICT(id) DO NOTHING;
                    """

                addParameter command "$id" (WorkPlanId.text plan.Id)
                addParameter command "$run" (RunId.text plan.RunId)
                addParameter command "$experiment" (ExperimentId.text plan.ExperimentId)
                addParameter command "$objective" plan.Objective
                addParameter command "$plan" (serializeWorkPlan plan)
                addParameter command "$created" (plan.CreatedAt.ToString("o"))
                addParameter command "$updated" timestamp
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.work_plan_save_failed"
                            "Could not persist the typed work plan."
                            exceptionValue
                    )
        }

    let updateWorkPlanStatus store planId status (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()
                command.CommandText <- "UPDATE work_plans SET status = $status, updated_at = $updated WHERE id = $id;"
                addParameter command "$id" (WorkPlanId.text planId)
                addParameter command "$status" status
                addParameter command "$updated" (DateTimeOffset.UtcNow.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.work_plan_status_failed"
                            "Could not update the typed work plan status."
                            exceptionValue
                    )
        }

    let loadWorkPlans store runId : Result<StoredWorkPlan list, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                "SELECT id, run_id, experiment_id, objective, plan_json, status, created_at, updated_at FROM work_plans WHERE run_id = $run ORDER BY created_at;"

            addParameter command "$run" (RunId.text runId)
            use reader = command.ExecuteReader()
            let plans = ResizeArray<StoredWorkPlan>()

            while reader.Read() do
                plans.Add
                    { Id = WorkPlanId.ofGuid (Guid.Parse(reader.GetString 0))
                      RunId = RunId.ofGuid (Guid.Parse(reader.GetString 1))
                      ExperimentId = ExperimentId.ofGuid (Guid.Parse(reader.GetString 2))
                      Objective = reader.GetString 3
                      PlanJson = reader.GetString 4
                      Status = reader.GetString 5
                      CreatedAt = DateTimeOffset.Parse(reader.GetString 6)
                      UpdatedAt = DateTimeOffset.Parse(reader.GetString 7) }

            Ok(List.ofSeq plans)
        with exceptionValue ->
            Error(
                persistenceError
                    "sqlite.work_plans_load_failed"
                    "Could not load persisted typed work plans."
                    exceptionValue
            )

    let private knowledgeEntityKindText kind =
        match kind with
        | KnowledgeEntityKind.Repository -> "repository"
        | KnowledgeEntityKind.File -> "file"
        | KnowledgeEntityKind.Symbol -> "symbol"
        | KnowledgeEntityKind.Experiment -> "experiment"
        | KnowledgeEntityKind.Metric -> "metric"
        | KnowledgeEntityKind.Hypothesis -> "hypothesis"
        | KnowledgeEntityKind.Constraint -> "constraint"
        | KnowledgeEntityKind.Concept -> "concept"

    let private parseKnowledgeEntityKind value =
        match value with
        | "repository" -> KnowledgeEntityKind.Repository
        | "file" -> KnowledgeEntityKind.File
        | "symbol" -> KnowledgeEntityKind.Symbol
        | "experiment" -> KnowledgeEntityKind.Experiment
        | "metric" -> KnowledgeEntityKind.Metric
        | "hypothesis" -> KnowledgeEntityKind.Hypothesis
        | "constraint" -> KnowledgeEntityKind.Constraint
        | _ -> KnowledgeEntityKind.Concept

    let private knowledgeSourceKindText kind =
        match kind with
        | KnowledgeSourceKind.Artifact -> "artifact"
        | KnowledgeSourceKind.Evaluation -> "evaluation"
        | KnowledgeSourceKind.Commit -> "commit"
        | KnowledgeSourceKind.AgentObservation -> "agent-observation"
        | KnowledgeSourceKind.UserStatement -> "user-statement"

    let private parseKnowledgeSourceKind value =
        match value with
        | "artifact" -> KnowledgeSourceKind.Artifact
        | "evaluation" -> KnowledgeSourceKind.Evaluation
        | "commit" -> KnowledgeSourceKind.Commit
        | "user-statement" -> KnowledgeSourceKind.UserStatement
        | _ -> KnowledgeSourceKind.AgentObservation

    let saveKnowledgeEntity store runId (entity: KnowledgeEntity) (_: CancellationToken) =
        async {
            try
                let attributes =
                    entity.Attributes |> Map.toArray |> dict |> JsonSerializer.Serialize

                use database = connection store
                database.Open()
                use transaction = database.BeginTransaction()
                use entityCommand = database.CreateCommand()
                entityCommand.Transaction <- transaction

                entityCommand.CommandText <-
                    """
                    INSERT INTO knowledge_entities(run_id, id, kind, canonical_name, attributes_json)
                    VALUES ($run, $id, $kind, $name, $attributes)
                    ON CONFLICT(run_id, id) DO UPDATE SET
                        kind = excluded.kind,
                        canonical_name = excluded.canonical_name,
                        attributes_json = excluded.attributes_json;
                    """

                addParameter entityCommand "$run" (RunId.text runId)
                addParameter entityCommand "$id" (KnowledgeEntityId.text entity.Id)
                addParameter entityCommand "$kind" (knowledgeEntityKindText entity.Kind)
                addParameter entityCommand "$name" entity.CanonicalName
                addParameter entityCommand "$attributes" attributes
                entityCommand.ExecuteNonQuery() |> ignore

                use aliasCommand = database.CreateCommand()
                aliasCommand.Transaction <- transaction

                aliasCommand.CommandText <-
                    """
                    INSERT INTO knowledge_aliases(run_id, entity_id, alias, normalized_alias)
                    VALUES ($run, $entity, $alias, $normalized)
                    ON CONFLICT(run_id, entity_id, normalized_alias) DO UPDATE SET alias = excluded.alias;
                    """

                addParameter aliasCommand "$run" (RunId.text runId)
                addParameter aliasCommand "$entity" (KnowledgeEntityId.text entity.Id)
                addParameter aliasCommand "$alias" entity.CanonicalName
                addParameter aliasCommand "$normalized" (KnowledgeGraph.normalizeAlias entity.CanonicalName)
                aliasCommand.ExecuteNonQuery() |> ignore
                transaction.Commit()
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.knowledge_entity_save_failed"
                            "Could not persist a knowledge entity."
                            exceptionValue
                    )
        }

    let saveKnowledgeAlias store runId entityId alias (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    """
                    INSERT INTO knowledge_aliases(run_id, entity_id, alias, normalized_alias)
                    VALUES ($run, $entity, $alias, $normalized)
                    ON CONFLICT(run_id, entity_id, normalized_alias) DO UPDATE SET alias = excluded.alias;
                    """

                addParameter command "$run" (RunId.text runId)
                addParameter command "$entity" (KnowledgeEntityId.text entityId)
                addParameter command "$alias" alias
                addParameter command "$normalized" (KnowledgeGraph.normalizeAlias alias)
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.knowledge_alias_save_failed"
                            "Could not persist a knowledge alias."
                            exceptionValue
                    )
        }

    let saveKnowledgeSource store (source: KnowledgeSource) (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    """
                    INSERT INTO knowledge_sources(
                        id, run_id, experiment_id, kind, location, sha256, captured_at)
                    VALUES ($id, $run, $experiment, $kind, $location, $sha256, $captured)
                    ON CONFLICT(id) DO UPDATE SET
                        kind = excluded.kind,
                        location = excluded.location,
                        sha256 = excluded.sha256;
                    """

                addParameter command "$id" (KnowledgeSourceId.text source.Id)
                addParameter command "$run" (RunId.text source.RunId)

                addParameter
                    command
                    "$experiment"
                    (source.ExperimentId
                     |> Option.map ExperimentId.text
                     |> Option.map box
                     |> Option.defaultValue DBNull.Value)

                addParameter command "$kind" (knowledgeSourceKindText source.Kind)
                addParameter command "$location" source.Location
                addParameter command "$sha256" (source.Sha256 |> Option.map box |> Option.defaultValue DBNull.Value)
                addParameter command "$captured" (source.CapturedAt.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.knowledge_source_save_failed"
                            "Could not persist a knowledge source."
                            exceptionValue
                    )
        }

    let private knowledgeValueParts value =
        match value with
        | KnowledgeValue.Entity entityId -> "entity", KnowledgeEntityId.text entityId
        | KnowledgeValue.Text text -> "text", text
        | KnowledgeValue.Number number -> "number", number.ToString(CultureInfo.InvariantCulture)
        | KnowledgeValue.Flag flag -> "flag", flag.ToString(CultureInfo.InvariantCulture)

    let private parseKnowledgeValue (kind: string) (value: string) =
        match kind with
        | "entity" -> KnowledgeValue.Entity(KnowledgeEntityId.ofGuid (Guid.Parse value))
        | "number" -> KnowledgeValue.Number(Decimal.Parse(value, CultureInfo.InvariantCulture))
        | "flag" -> KnowledgeValue.Flag(Boolean.Parse value)
        | _ -> KnowledgeValue.Text value

    let saveKnowledgeClaim store (claim: KnowledgeClaim) (_: CancellationToken) =
        async {
            try
                if Set.isEmpty claim.Sources then
                    invalidArg (nameof claim) "Knowledge claims require provenance sources."

                if claim.Confidence < 0M || claim.Confidence > 1M then
                    invalidArg (nameof claim) "Knowledge claim confidence must be between zero and one."

                let objectKind, objectValue = knowledgeValueParts claim.Object
                use database = connection store
                database.Open()
                use transaction = database.BeginTransaction()
                use claimCommand = database.CreateCommand()
                claimCommand.Transaction <- transaction

                claimCommand.CommandText <-
                    """
                    INSERT INTO knowledge_claims(
                        id, run_id, subject_id, predicate, object_kind, object_value,
                        confidence, supersedes_id, created_at)
                    VALUES ($id, $run, $subject, $predicate, $objectKind, $objectValue,
                            $confidence, $supersedes, $created)
                    ON CONFLICT(id) DO NOTHING;
                    """

                addParameter claimCommand "$id" (KnowledgeClaimId.text claim.Id)
                addParameter claimCommand "$run" (RunId.text claim.RunId)
                addParameter claimCommand "$subject" (KnowledgeEntityId.text claim.Subject)
                addParameter claimCommand "$predicate" claim.Predicate
                addParameter claimCommand "$objectKind" objectKind
                addParameter claimCommand "$objectValue" objectValue
                addParameter claimCommand "$confidence" (claim.Confidence.ToString(CultureInfo.InvariantCulture))

                addParameter
                    claimCommand
                    "$supersedes"
                    (claim.Supersedes
                     |> Option.map KnowledgeClaimId.text
                     |> Option.map box
                     |> Option.defaultValue DBNull.Value)

                addParameter claimCommand "$created" (claim.CreatedAt.ToString("o"))
                claimCommand.ExecuteNonQuery() |> ignore

                for sourceId in claim.Sources do
                    use sourceCommand = database.CreateCommand()
                    sourceCommand.Transaction <- transaction

                    sourceCommand.CommandText <-
                        "INSERT OR IGNORE INTO knowledge_claim_sources(claim_id, source_id) VALUES ($claim, $source);"

                    addParameter sourceCommand "$claim" (KnowledgeClaimId.text claim.Id)
                    addParameter sourceCommand "$source" (KnowledgeSourceId.text sourceId)
                    sourceCommand.ExecuteNonQuery() |> ignore

                transaction.Commit()
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.knowledge_claim_save_failed"
                            "Could not persist a provenance-aware knowledge claim."
                            exceptionValue
                    )
        }

    let loadKnowledgeGraph store runId : Result<KnowledgeGraph, HarnessError> =
        try
            use database = connection store
            database.Open()

            use entityCommand = database.CreateCommand()

            entityCommand.CommandText <-
                "SELECT id, kind, canonical_name, attributes_json FROM knowledge_entities WHERE run_id = $run;"

            addParameter entityCommand "$run" (RunId.text runId)
            use entityReader = entityCommand.ExecuteReader()
            let mutable entities = Map.empty

            while entityReader.Read() do
                use attributesDocument = JsonDocument.Parse(entityReader.GetString 3)

                let attributes =
                    attributesDocument.RootElement.EnumerateObject()
                    |> Seq.map (fun property -> property.Name, property.Value.GetString())
                    |> Map.ofSeq

                let entity =
                    { Id = KnowledgeEntityId.ofGuid (Guid.Parse(entityReader.GetString 0))
                      Kind = parseKnowledgeEntityKind (entityReader.GetString 1)
                      CanonicalName = entityReader.GetString 2
                      Attributes = attributes }

                entities <- entities |> Map.add entity.Id entity

            entityReader.Close()
            use aliasCommand = database.CreateCommand()

            aliasCommand.CommandText <- "SELECT entity_id, normalized_alias FROM knowledge_aliases WHERE run_id = $run;"

            addParameter aliasCommand "$run" (RunId.text runId)
            use aliasReader = aliasCommand.ExecuteReader()
            let mutable aliases = Map.empty

            while aliasReader.Read() do
                let entityId = KnowledgeEntityId.ofGuid (Guid.Parse(aliasReader.GetString 0))
                let alias = aliasReader.GetString 1

                aliases <-
                    aliases
                    |> Map.change alias (fun current ->
                        current |> Option.defaultValue Set.empty |> Set.add entityId |> Some)

            aliasReader.Close()
            use sourceCommand = database.CreateCommand()

            sourceCommand.CommandText <-
                "SELECT id, experiment_id, kind, location, sha256, captured_at FROM knowledge_sources WHERE run_id = $run;"

            addParameter sourceCommand "$run" (RunId.text runId)
            use sourceReader = sourceCommand.ExecuteReader()
            let mutable sources = Map.empty

            while sourceReader.Read() do
                let source =
                    { Id = KnowledgeSourceId.ofGuid (Guid.Parse(sourceReader.GetString 0))
                      RunId = runId
                      ExperimentId =
                        if sourceReader.IsDBNull 1 then
                            None
                        else
                            Some(ExperimentId.ofGuid (Guid.Parse(sourceReader.GetString 1)))
                      Kind = parseKnowledgeSourceKind (sourceReader.GetString 2)
                      Location = sourceReader.GetString 3
                      Sha256 =
                        if sourceReader.IsDBNull 4 then
                            None
                        else
                            Some(sourceReader.GetString 4)
                      CapturedAt = DateTimeOffset.Parse(sourceReader.GetString 5) }

                sources <- sources |> Map.add source.Id source

            sourceReader.Close()
            use provenanceCommand = database.CreateCommand()

            provenanceCommand.CommandText <- "SELECT claim_id, source_id FROM knowledge_claim_sources;"

            use provenanceReader = provenanceCommand.ExecuteReader()
            let mutable provenance = Map.empty

            while provenanceReader.Read() do
                let claimId = KnowledgeClaimId.ofGuid (Guid.Parse(provenanceReader.GetString 0))
                let sourceId = KnowledgeSourceId.ofGuid (Guid.Parse(provenanceReader.GetString 1))

                provenance <-
                    provenance
                    |> Map.change claimId (fun current ->
                        current |> Option.defaultValue Set.empty |> Set.add sourceId |> Some)

            provenanceReader.Close()
            use claimCommand = database.CreateCommand()

            claimCommand.CommandText <-
                "SELECT id, subject_id, predicate, object_kind, object_value, confidence, supersedes_id, created_at FROM knowledge_claims WHERE run_id = $run;"

            addParameter claimCommand "$run" (RunId.text runId)
            use claimReader = claimCommand.ExecuteReader()
            let mutable claims = Map.empty

            while claimReader.Read() do
                let claimId = KnowledgeClaimId.ofGuid (Guid.Parse(claimReader.GetString 0))

                let claim =
                    { Id = claimId
                      RunId = runId
                      Subject = KnowledgeEntityId.ofGuid (Guid.Parse(claimReader.GetString 1))
                      Predicate = claimReader.GetString 2
                      Object = parseKnowledgeValue (claimReader.GetString 3) (claimReader.GetString 4)
                      Confidence = Decimal.Parse(claimReader.GetString 5, CultureInfo.InvariantCulture)
                      Sources = provenance |> Map.tryFind claimId |> Option.defaultValue Set.empty
                      Supersedes =
                        if claimReader.IsDBNull 6 then
                            None
                        else
                            Some(KnowledgeClaimId.ofGuid (Guid.Parse(claimReader.GetString 6)))
                      CreatedAt = DateTimeOffset.Parse(claimReader.GetString 7) }

                claims <- claims |> Map.add claim.Id claim

            Ok
                { Entities = entities
                  Aliases = aliases
                  Sources = sources
                  Claims = claims }
        with exceptionValue ->
            Error(
                persistenceError
                    "sqlite.knowledge_graph_load_failed"
                    "Could not load the provenance-aware knowledge graph."
                    exceptionValue
            )

    let private annotationTargetParts target : string * obj =
        match target with
        | AnnotationTarget.Run -> "run", box DBNull.Value
        | AnnotationTarget.Experiment experimentId -> "experiment", box (ExperimentId.text experimentId)
        | AnnotationTarget.Claim claimId -> "claim", box (KnowledgeClaimId.text claimId)

    let saveAnnotation store (annotation: RunAnnotation) (_: CancellationToken) =
        async {
            try
                let targetKind, targetId = annotationTargetParts annotation.Target
                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    """
                    INSERT INTO run_annotations(id, run_id, target_kind, target_id, author, body, created_at)
                    VALUES ($id, $run, $targetKind, $targetId, $author, $body, $created);
                    """

                addParameter command "$id" (RunAnnotationId.text annotation.Id)
                addParameter command "$run" (RunId.text annotation.RunId)
                addParameter command "$targetKind" targetKind
                addParameter command "$targetId" targetId
                addParameter command "$author" annotation.Author
                addParameter command "$body" annotation.Body
                addParameter command "$created" (annotation.CreatedAt.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.annotation_save_failed"
                            "Could not persist the run annotation."
                            exceptionValue
                    )
        }

    let loadAnnotations store runId : Result<RunAnnotation list, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                "SELECT id, target_kind, target_id, author, body, created_at FROM run_annotations WHERE run_id = $run ORDER BY created_at, id;"

            addParameter command "$run" (RunId.text runId)
            use reader = command.ExecuteReader()
            let annotations = ResizeArray<RunAnnotation>()

            while reader.Read() do
                let target =
                    match reader.GetString 1 with
                    | "experiment" -> AnnotationTarget.Experiment(ExperimentId.ofGuid (Guid.Parse(reader.GetString 2)))
                    | "claim" -> AnnotationTarget.Claim(KnowledgeClaimId.ofGuid (Guid.Parse(reader.GetString 2)))
                    | _ -> AnnotationTarget.Run

                annotations.Add
                    { Id = RunAnnotationId.ofGuid (Guid.Parse(reader.GetString 0))
                      RunId = runId
                      Target = target
                      Author = reader.GetString 3
                      Body = reader.GetString 4
                      CreatedAt = DateTimeOffset.Parse(reader.GetString 5) }

            Ok(List.ofSeq annotations)
        with exceptionValue ->
            Error(persistenceError "sqlite.annotations_load_failed" "Could not load run annotations." exceptionValue)

    let appendEvent store runId experimentId kind payload (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    "INSERT INTO events(run_id, experiment_id, kind, payload, created_at) VALUES ($run, $experiment, $kind, $payload, $created);"

                addParameter command "$run" (RunId.text runId)

                addParameter
                    command
                    "$experiment"
                    (experimentId
                     |> Option.map ExperimentId.text
                     |> Option.map box
                     |> Option.defaultValue DBNull.Value)

                addParameter command "$kind" kind
                addParameter command "$payload" payload
                addParameter command "$created" (DateTimeOffset.UtcNow.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return Error(persistenceError "sqlite.event_failed" "Could not append a journal event." exceptionValue)
        }

    let saveUsage store runId experimentId usage (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    """
                    INSERT INTO usage(run_id, experiment_id, input_tokens, cached_input_tokens, output_tokens, reasoning_output_tokens)
                    VALUES ($run, $experiment, $input, $cached, $output, $reasoning)
                    ON CONFLICT(run_id, experiment_id) DO UPDATE SET
                        input_tokens = excluded.input_tokens,
                        cached_input_tokens = excluded.cached_input_tokens,
                        output_tokens = excluded.output_tokens,
                        reasoning_output_tokens = excluded.reasoning_output_tokens;
                    """

                let value selector =
                    usage
                    |> Option.map selector
                    |> Option.map box
                    |> Option.defaultValue DBNull.Value

                addParameter command "$run" (RunId.text runId)
                addParameter command "$experiment" (ExperimentId.text experimentId)
                addParameter command "$input" (value _.InputTokens)
                addParameter command "$cached" (value _.CachedInputTokens)
                addParameter command "$output" (value _.OutputTokens)
                addParameter command "$reasoning" (value _.ReasoningOutputTokens)
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return Error(persistenceError "sqlite.usage_failed" "Could not save token usage." exceptionValue)
        }

    let saveMemory store runId (memory: MemorySummary) (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    """
                    INSERT INTO memories(
                        run_id, experiment_id, outcome, metric, hypothesis, change_summary,
                        expected_effect, validation_notes_json, reusable_lesson, created_at)
                    VALUES ($run, $experiment, $outcome, $metric, $hypothesis, $change,
                            $effect, $notes, $lesson, $created)
                    ON CONFLICT(run_id, experiment_id) DO UPDATE SET
                        outcome = excluded.outcome,
                        metric = excluded.metric,
                        hypothesis = excluded.hypothesis,
                        change_summary = excluded.change_summary,
                        expected_effect = excluded.expected_effect,
                        validation_notes_json = excluded.validation_notes_json,
                        reusable_lesson = excluded.reusable_lesson;
                    """

                addParameter command "$run" (RunId.text runId)
                addParameter command "$experiment" (ExperimentId.text memory.ExperimentId)
                addParameter command "$outcome" memory.Outcome

                addParameter
                    command
                    "$metric"
                    (memory.Metric
                     |> Option.map string
                     |> Option.map box
                     |> Option.defaultValue DBNull.Value)

                addParameter command "$hypothesis" memory.Summary.Hypothesis
                addParameter command "$change" memory.Summary.ChangeSummary
                addParameter command "$effect" memory.Summary.ExpectedEffect
                addParameter command "$notes" (JsonSerializer.Serialize memory.Summary.ValidationNotes)
                addParameter command "$lesson" memory.Summary.ReusableLesson
                addParameter command "$created" (DateTimeOffset.UtcNow.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return Error(persistenceError "sqlite.memory_failed" "Could not save experiment memory." exceptionValue)
        }

    let loadMemories
        store
        runId
        maxCount
        maxCharacters
        (_: CancellationToken)
        : Async<Result<MemorySummary list, HarnessError>> =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    """
                    SELECT experiment_id, outcome, metric, hypothesis, change_summary,
                           expected_effect, validation_notes_json, reusable_lesson
                    FROM memories
                    WHERE run_id = $run
                    ORDER BY created_at DESC
                    LIMIT $limit;
                    """

                addParameter command "$run" (RunId.text runId)
                addParameter command "$limit" (max maxCount (maxCount * 3))
                use reader = command.ExecuteReader()
                let results = ResizeArray<MemorySummary>()
                let mutable characterCount = 0

                while reader.Read() && results.Count < maxCount && characterCount < maxCharacters do
                    let summary =
                        { Hypothesis = reader.GetString 3
                          ChangeSummary = reader.GetString 4
                          ExpectedEffect = reader.GetString 5
                          ValidationNotes = JsonSerializer.Deserialize<string list>(reader.GetString 6)
                          ReusableLesson = reader.GetString 7 }

                    let length =
                        summary.Hypothesis.Length
                        + summary.ChangeSummary.Length
                        + summary.ExpectedEffect.Length
                        + summary.ReusableLesson.Length

                    if characterCount + length <= maxCharacters then
                        let metric =
                            if reader.IsDBNull 2 then
                                None
                            else
                                match Decimal.TryParse(reader.GetString 2) with
                                | true, value -> Some value
                                | false, _ -> None

                        results.Add
                            { ExperimentId = ExperimentId.ofGuid (Guid.Parse(reader.GetString 0))
                              Outcome = reader.GetString 1
                              Metric = metric
                              Summary = summary }

                        characterCount <- characterCount + length

                return Ok(List.ofSeq results)
            with exceptionValue ->
                return
                    Error(
                        persistenceError "sqlite.memory_load_failed" "Could not load experiment memory." exceptionValue
                    )
        }

    let loadEvents store limit =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                "SELECT sequence, run_id, experiment_id, kind, payload, created_at FROM events ORDER BY sequence DESC LIMIT $limit;"

            addParameter command "$limit" limit
            use reader = command.ExecuteReader()
            let events = ResizeArray<HistoryEvent>()

            while reader.Read() do
                events.Add
                    { Sequence = reader.GetInt64 0
                      RunId = RunId.ofGuid (Guid.Parse(reader.GetString 1))
                      ExperimentId =
                        if reader.IsDBNull 2 then
                            None
                        else
                            Some(ExperimentId.ofGuid (Guid.Parse(reader.GetString 2)))
                      Kind = reader.GetString 3
                      Payload = reader.GetString 4
                      CreatedAt = DateTimeOffset.Parse(reader.GetString 5) }

            Ok(List.ofSeq events)
        with exceptionValue ->
            Error(persistenceError "sqlite.history_failed" "Could not load run history." exceptionValue)

    let loadEventsForRun store runId : Result<HistoryEvent list, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                "SELECT sequence, run_id, experiment_id, kind, payload, created_at FROM events WHERE run_id = $run ORDER BY sequence;"

            addParameter command "$run" (RunId.text runId)
            use reader = command.ExecuteReader()
            let events = ResizeArray<HistoryEvent>()

            while reader.Read() do
                events.Add
                    { Sequence = reader.GetInt64 0
                      RunId = RunId.ofGuid (Guid.Parse(reader.GetString 1))
                      ExperimentId =
                        if reader.IsDBNull 2 then
                            None
                        else
                            Some(ExperimentId.ofGuid (Guid.Parse(reader.GetString 2)))
                      Kind = reader.GetString 3
                      Payload = reader.GetString 4
                      CreatedAt = DateTimeOffset.Parse(reader.GetString 5) }

            Ok(List.ofSeq events)
        with exceptionValue ->
            Error(
                persistenceError
                    "sqlite.run_history_failed"
                    "Could not load the selected run's event history."
                    exceptionValue
            )

    let loadArtifactsForRun store runId : Result<StoredArtifact list, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                "SELECT id, run_id, experiment_id, kind, path, sha256, created_at FROM artifacts WHERE run_id = $run ORDER BY id;"

            addParameter command "$run" (RunId.text runId)
            use reader = command.ExecuteReader()
            let artifacts = ResizeArray<StoredArtifact>()

            while reader.Read() do
                artifacts.Add
                    { Id = reader.GetInt64 0
                      RunId = RunId.ofGuid (Guid.Parse(reader.GetString 1))
                      ExperimentId =
                        if reader.IsDBNull 2 then
                            None
                        else
                            Some(ExperimentId.ofGuid (Guid.Parse(reader.GetString 2)))
                      Kind = reader.GetString 3
                      Path = reader.GetString 4
                      Sha256 = if reader.IsDBNull 5 then None else Some(reader.GetString 5)
                      CreatedAt = DateTimeOffset.Parse(reader.GetString 6) }

            Ok(List.ofSeq artifacts)
        with exceptionValue ->
            Error(
                persistenceError
                    "sqlite.artifacts_load_failed"
                    "Could not load persisted artifact metadata."
                    exceptionValue
            )

    let verifyArtifact (artifact: StoredArtifact) =
        match artifact.Sha256 with
        | None -> Error $"Artifact '{artifact.Path}' has no persisted hash."
        | Some _ when not (File.Exists artifact.Path) -> Error $"Artifact '{artifact.Path}' is missing."
        | Some expected ->
            let actual = AtomicFile.sha256 artifact.Path

            if String.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) then
                Ok()
            else
                Error $"Artifact '{artifact.Path}' failed SHA-256 verification."

    let loadPendingOperations store runId : Result<DurableOperation list, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                """
                SELECT run_id, experiment_id, kind, status, payload, created_at, updated_at
                FROM durable_operations
                WHERE run_id = $run AND status = 'pending'
                ORDER BY created_at;
                """

            addParameter command "$run" (RunId.text runId)
            use reader = command.ExecuteReader()
            let operations = ResizeArray<DurableOperation>()

            while reader.Read() do
                operations.Add
                    { RunId = RunId.ofGuid (Guid.Parse(reader.GetString 0))
                      ExperimentId = ExperimentId.ofGuid (Guid.Parse(reader.GetString 1))
                      Kind = reader.GetString 2
                      Status = reader.GetString 3
                      Payload = reader.GetString 4
                      CreatedAt = DateTimeOffset.Parse(reader.GetString 5)
                      UpdatedAt = DateTimeOffset.Parse(reader.GetString 6) }

            Ok(List.ofSeq operations)
        with exceptionValue ->
            Error(
                persistenceError
                    "sqlite.operations_load_failed"
                    "Could not load pending durable operations."
                    exceptionValue
            )

    let private nullableString (reader: SqliteDataReader) index =
        if reader.IsDBNull index then
            None
        else
            Some(reader.GetString index)

    let loadRuns store : Result<StoredRun list, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                "SELECT runs.id, projects.source_path, runs.status, runs.config_json, runs.created_at, runs.updated_at FROM runs LEFT JOIN projects ON projects.id = runs.project_id ORDER BY runs.created_at DESC;"

            use reader = command.ExecuteReader()
            let results = ResizeArray<StoredRun>()

            while reader.Read() do
                results.Add
                    { Id = RunId.ofGuid (Guid.Parse(reader.GetString 0))
                      SourcePath = if reader.IsDBNull 1 then "" else reader.GetString 1
                      Status = reader.GetString 2
                      ConfigJson = nullableString reader 3
                      CreatedAt = DateTimeOffset.Parse(reader.GetString 4)
                      UpdatedAt = DateTimeOffset.Parse(reader.GetString 5) }

            Ok(List.ofSeq results)
        with
        | :? SqliteException as exceptionValue when exceptionValue.SqliteErrorCode = 1 -> Ok []
        | exceptionValue ->
            Error(persistenceError "sqlite.runs_load_failed" "Could not load persisted runs." exceptionValue)

    let loadExperiments store runId : Result<StoredExperiment list, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                "SELECT id, run_id, sequence, parent_oid, candidate_oid, outcome, created_at, updated_at FROM experiments WHERE run_id = $run ORDER BY sequence, created_at;"

            addParameter command "$run" (RunId.text runId)
            use reader = command.ExecuteReader()
            let results = ResizeArray<StoredExperiment>()

            while reader.Read() do
                results.Add
                    { Id = ExperimentId.ofGuid (Guid.Parse(reader.GetString 0))
                      RunId = RunId.ofGuid (Guid.Parse(reader.GetString 1))
                      Sequence = reader.GetInt32 2
                      Parent = nullableString reader 3 |> Option.map CommitOid.create
                      Candidate = nullableString reader 4 |> Option.map CommitOid.create
                      Outcome = reader.GetString 5
                      CreatedAt = DateTimeOffset.Parse(reader.GetString 6)
                      UpdatedAt = DateTimeOffset.Parse(reader.GetString 7) }

            Ok(List.ofSeq results)
        with
        | :? SqliteException as exceptionValue when exceptionValue.SqliteErrorCode = 1 -> Ok []
        | exceptionValue ->
            Error(
                persistenceError
                    "sqlite.experiments_load_failed"
                    "Could not load persisted experiment lineage."
                    exceptionValue
            )

    let loadEvaluationsForRun store runId : Result<StoredEvaluation list, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                "SELECT experiment_id, result_json, created_at FROM evaluations WHERE run_id = $run ORDER BY created_at;"

            addParameter command "$run" (RunId.text runId)
            use reader = command.ExecuteReader()
            let results = ResizeArray<StoredEvaluation>()

            while reader.Read() do
                if not (reader.IsDBNull 1) && not (reader.IsDBNull 0) then
                    results.Add
                        { ExperimentId = ExperimentId.ofGuid (Guid.Parse(reader.GetString 0))
                          ResultJson = reader.GetString 1
                          CreatedAt = DateTimeOffset.Parse(reader.GetString 2) }

            Ok(List.ofSeq results)
        with
        | :? SqliteException as exceptionValue when exceptionValue.SqliteErrorCode = 1 -> Ok []
        | exceptionValue ->
            Error(
                persistenceError
                    "sqlite.evaluations_load_failed"
                    "Could not load persisted evaluation results."
                    exceptionValue
            )

    let loadUsageForRun store runId : Result<StoredUsage list, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                "SELECT experiment_id, input_tokens, cached_input_tokens, output_tokens, reasoning_output_tokens FROM usage WHERE run_id = $run;"

            addParameter command "$run" (RunId.text runId)
            use reader = command.ExecuteReader()
            let results = ResizeArray<StoredUsage>()

            while reader.Read() do
                let value index =
                    if reader.IsDBNull index then
                        None
                    else
                        Some(reader.GetInt64 index)

                let usage =
                    match value 1, value 2, value 3, value 4 with
                    | Some input, Some cached, Some output, Some reasoning ->
                        Some(
                            TokenUsage.normalize
                                { InputTokens = input
                                  CachedInputTokens = cached
                                  OutputTokens = output
                                  ReasoningOutputTokens = reasoning }
                        )
                    | _ -> None

                results.Add
                    { ExperimentId = ExperimentId.ofGuid (Guid.Parse(reader.GetString 0))
                      Usage = usage }

            Ok(List.ofSeq results)
        with
        | :? SqliteException as exceptionValue when exceptionValue.SqliteErrorCode = 1 -> Ok []
        | exceptionValue ->
            Error(persistenceError "sqlite.usage_load_failed" "Could not load persisted token usage." exceptionValue)

    let loadEvolutionMemories store runId : Result<MemorySummary list, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                """
                SELECT experiment_id, outcome, metric, hypothesis, change_summary,
                       expected_effect, validation_notes_json, reusable_lesson
                FROM memories WHERE run_id = $run ORDER BY created_at;
                """

            addParameter command "$run" (RunId.text runId)
            use reader = command.ExecuteReader()
            let results = ResizeArray<MemorySummary>()

            while reader.Read() do
                let metric =
                    if reader.IsDBNull 2 then
                        None
                    else
                        match Decimal.TryParse(reader.GetString 2) with
                        | true, value -> Some value
                        | false, _ -> None

                results.Add
                    { ExperimentId = ExperimentId.ofGuid (Guid.Parse(reader.GetString 0))
                      Outcome = reader.GetString 1
                      Metric = metric
                      Summary =
                        { Hypothesis = reader.GetString 3
                          ChangeSummary = reader.GetString 4
                          ExpectedEffect = reader.GetString 5
                          ValidationNotes = JsonSerializer.Deserialize<string list>(reader.GetString 6)
                          ReusableLesson = reader.GetString 7 } }

            Ok(List.ofSeq results)
        with
        | :? SqliteException as exceptionValue when exceptionValue.SqliteErrorCode = 1 -> Ok []
        | exceptionValue ->
            Error(
                persistenceError
                    "sqlite.evolution_memories_load_failed"
                    "Could not load persisted experiment summaries."
                    exceptionValue
            )

    let countDuplicateHypotheses store runId =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                "SELECT COUNT(*) - COUNT(DISTINCT lower(trim(hypothesis))) FROM memories WHERE run_id = $run;"

            addParameter command "$run" (RunId.text runId)
            Ok(command.ExecuteScalar() |> Convert.ToInt32)
        with exceptionValue ->
            Error(
                persistenceError
                    "sqlite.duplicate_hypotheses_failed"
                    "Could not count duplicate hypotheses."
                    exceptionValue
            )

    let journalPort store =
        { Initialize = initialize store
          AppendEvent = appendEvent store
          SaveUsage = saveUsage store
          LoadMemories = loadMemories store
          SaveMemory = saveMemory store }

    let memoryPort store =
        { Select = loadMemories store
          Append = saveMemory store }
