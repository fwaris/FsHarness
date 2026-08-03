namespace FsHarness.Infrastructure

open System
open System.Data
open System.IO
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
                    CREATE TABLE IF NOT EXISTS schema_info (
                        version INTEGER NOT NULL
                    );
                    INSERT INTO schema_info(version)
                    SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_info);
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
                   direction = string config.Metric.Direction
                   minDelta = config.Metric.MinDelta
                   target = config.Metric.Target |> Option.map box |> Option.defaultValue null
                   comparison = string config.Metric.Comparison |}
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
               promotionMode = string config.PromotionMode |}

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
                addParameter command "$parent" (parent |> Option.map CommitOid.value |> Option.defaultValue null)
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
