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
      Usage: TokenUsage option
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

type StoredExperimentEdge =
    { Id: string
      RunId: RunId
      ExperimentId: ExperimentId
      Parent: CommitOid
      Child: CommitOid option
      Kind: ExperimentKind
      Role: ExperimentParentRole
      CreatedAt: DateTimeOffset }

type StoredExperimentGraphState =
    { RunId: RunId
      ExperimentId: ExperimentId
      Kind: ExperimentKind
      Validity: EvaluationValidity
      ChampionDecision: ChampionDecision
      SearchStatus: SearchStatus
      ChampionAtStart: CommitOid
      HypothesisFamily: string
      SynthesisDepth: int
      UpdatedAt: DateTimeOffset }

type StoredSearchHead =
    { RunId: RunId
      Commit: CommitOid
      Round: int
      Status: SearchStatus
      UpdatedAt: DateTimeOffset }

type StoredSearchRound =
    { RunId: RunId
      Round: int
      Heads: CommitOid list
      CompletedHeads: Set<CommitOid>
      OrdinarySinceSynthesis: int
      Status: string
      UpdatedAt: DateTimeOffset }

type StoredSynthesisAttempt =
    { RunId: RunId
      Primary: CommitOid
      Contributor: CommitOid
      PolicyVersion: int
      Status: string
      ExperimentId: ExperimentId option
      FailureDetail: string option
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

                use versionCommand = database.CreateCommand()
                versionCommand.CommandText <- "PRAGMA user_version;"
                let previousVersion = versionCommand.ExecuteScalar() |> Convert.ToInt32

                if previousVersion > 0 && previousVersion < 6 then
                    let backupPath = store.DatabasePath + ".pre-v6.bak"

                    if not (File.Exists backupPath) then
                        let backupBuilder = SqliteConnectionStringBuilder()
                        backupBuilder.DataSource <- backupPath
                        backupBuilder.Mode <- SqliteOpenMode.ReadWriteCreate
                        // The backup connection is scoped to this migration. Do not
                        // return it to the process-wide pool while the temp database
                        // directory may be removed immediately after initialization.
                        backupBuilder.Pooling <- false
                        use backup = new SqliteConnection(backupBuilder.ToString())
                        backup.Open()
                        database.BackupDatabase backup

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
                    CREATE TABLE IF NOT EXISTS experiment_edges (
                        id TEXT PRIMARY KEY,
                        run_id TEXT NOT NULL,
                        experiment_id TEXT NOT NULL,
                        parent_oid TEXT NOT NULL,
                        child_oid TEXT,
                        edge_kind TEXT NOT NULL,
                        parent_role TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        UNIQUE(run_id, experiment_id, parent_oid, parent_role),
                        FOREIGN KEY(run_id) REFERENCES runs(id),
                        FOREIGN KEY(experiment_id) REFERENCES experiments(id)
                    );
                    CREATE INDEX IF NOT EXISTS experiment_edges_parent
                        ON experiment_edges(run_id, parent_oid, created_at);
                    CREATE INDEX IF NOT EXISTS experiment_edges_child
                        ON experiment_edges(run_id, child_oid, created_at);
                    CREATE TABLE IF NOT EXISTS experiment_graph_state (
                        run_id TEXT NOT NULL,
                        experiment_id TEXT PRIMARY KEY,
                        experiment_kind TEXT NOT NULL,
                        evaluation_validity TEXT NOT NULL,
                        validity_detail TEXT,
                        champion_decision TEXT NOT NULL,
                        search_status TEXT NOT NULL,
                        champion_at_start TEXT NOT NULL,
                        hypothesis_family TEXT NOT NULL,
                        synthesis_depth INTEGER NOT NULL,
                        updated_at TEXT NOT NULL,
                        FOREIGN KEY(run_id) REFERENCES runs(id),
                        FOREIGN KEY(experiment_id) REFERENCES experiments(id)
                    );
                    CREATE TABLE IF NOT EXISTS champion_history (
                        run_id TEXT NOT NULL,
                        sequence INTEGER NOT NULL,
                        commit_oid TEXT NOT NULL,
                        score TEXT,
                        experiment_id TEXT,
                        created_at TEXT NOT NULL,
                        PRIMARY KEY(run_id, sequence),
                        FOREIGN KEY(run_id) REFERENCES runs(id)
                    );
                    CREATE TABLE IF NOT EXISTS active_heads (
                        run_id TEXT NOT NULL,
                        commit_oid TEXT NOT NULL,
                        round INTEGER NOT NULL,
                        status TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        PRIMARY KEY(run_id, commit_oid),
                        FOREIGN KEY(run_id) REFERENCES runs(id)
                    );
                    CREATE INDEX IF NOT EXISTS active_heads_round
                        ON active_heads(run_id, round, status);
                    CREATE TABLE IF NOT EXISTS search_rounds (
                        run_id TEXT NOT NULL,
                        round INTEGER NOT NULL,
                        heads_json TEXT NOT NULL,
                        completed_heads_json TEXT NOT NULL,
                        ordinary_since_synthesis INTEGER NOT NULL,
                        status TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        PRIMARY KEY(run_id, round),
                        FOREIGN KEY(run_id) REFERENCES runs(id)
                    );
                    CREATE TABLE IF NOT EXISTS synthesis_attempts (
                        run_id TEXT NOT NULL,
                        primary_oid TEXT NOT NULL,
                        contributor_oid TEXT NOT NULL,
                        policy_version INTEGER NOT NULL,
                        status TEXT NOT NULL,
                        experiment_id TEXT,
                        detail TEXT,
                        updated_at TEXT NOT NULL,
                        PRIMARY KEY(run_id, primary_oid, contributor_oid, policy_version),
                        FOREIGN KEY(run_id) REFERENCES runs(id)
                    );
                    CREATE TABLE IF NOT EXISTS knowledge_graph_nodes (
                        project_id TEXT NOT NULL,
                        id TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        canonical_name TEXT NOT NULL,
                        attributes_json TEXT NOT NULL,
                        version INTEGER NOT NULL,
                        origin_run_id TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        PRIMARY KEY(project_id, id, version),
                        FOREIGN KEY(project_id) REFERENCES projects(id),
                        FOREIGN KEY(origin_run_id) REFERENCES runs(id)
                    );
                    CREATE INDEX IF NOT EXISTS knowledge_graph_nodes_current
                        ON knowledge_graph_nodes(project_id, id, version DESC);
                    CREATE TABLE IF NOT EXISTS knowledge_graph_edges (
                        id TEXT PRIMARY KEY,
                        project_id TEXT NOT NULL,
                        from_id TEXT NOT NULL,
                        relation TEXT NOT NULL,
                        to_id TEXT NOT NULL,
                        confidence TEXT NOT NULL,
                        is_inference INTEGER NOT NULL,
                        rationale TEXT,
                        source_ids_json TEXT NOT NULL,
                        origin_run_id TEXT NOT NULL,
                        valid_from TEXT NOT NULL,
                        valid_to TEXT,
                        FOREIGN KEY(project_id) REFERENCES projects(id),
                        FOREIGN KEY(origin_run_id) REFERENCES runs(id)
                    );
                    CREATE INDEX IF NOT EXISTS knowledge_graph_edges_from
                        ON knowledge_graph_edges(project_id, from_id, relation, valid_to);
                    CREATE INDEX IF NOT EXISTS knowledge_graph_edges_to
                        ON knowledge_graph_edges(project_id, to_id, relation, valid_to);
                    CREATE TABLE IF NOT EXISTS graph_updates (
                        idempotency_key TEXT PRIMARY KEY,
                        project_id TEXT NOT NULL,
                        run_id TEXT NOT NULL,
                        agent_id TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        FOREIGN KEY(project_id) REFERENCES projects(id),
                        FOREIGN KEY(run_id) REFERENCES runs(id)
                    );
                    INSERT OR IGNORE INTO experiment_edges(
                        id, run_id, experiment_id, parent_oid, child_oid,
                        edge_kind, parent_role, created_at)
                    SELECT run_id || ':' || id || ':primary', run_id, id, parent_oid,
                           candidate_oid, 'expanded-from', 'primary', created_at
                    FROM experiments
                    WHERE parent_oid IS NOT NULL;
                    INSERT OR IGNORE INTO champion_history(
                        run_id, sequence, commit_oid, score, experiment_id, created_at)
                    SELECT runs.id, 0,
                           COALESCE(
                               (SELECT candidate_oid FROM experiments accepted
                                WHERE accepted.run_id = runs.id
                                  AND accepted.outcome = 'Accepted'
                                  AND accepted.candidate_oid IS NOT NULL
                                ORDER BY accepted.sequence DESC LIMIT 1),
                               json_extract(runs.config_json, '$.baseCommit')),
                           NULL, NULL, runs.created_at
                    FROM runs
                    WHERE COALESCE(
                              (SELECT candidate_oid FROM experiments accepted
                               WHERE accepted.run_id = runs.id
                                 AND accepted.outcome = 'Accepted'
                                 AND accepted.candidate_oid IS NOT NULL
                               ORDER BY accepted.sequence DESC LIMIT 1),
                              json_extract(runs.config_json, '$.baseCommit')) IS NOT NULL;
                    INSERT OR IGNORE INTO champion_history(
                        run_id, sequence, commit_oid, score, experiment_id, created_at)
                    SELECT experiments.run_id, experiments.sequence, experiments.candidate_oid,
                           json_extract(evaluations.result_json,
                               '$.metrics.' || json_extract(runs.config_json, '$.metric.name')),
                           experiments.id, experiments.updated_at
                    FROM experiments
                    JOIN runs ON runs.id = experiments.run_id
                    LEFT JOIN evaluations ON evaluations.experiment_id = experiments.id
                    WHERE experiments.outcome = 'Accepted'
                      AND experiments.candidate_oid IS NOT NULL
                      AND experiments.sequence > 0;
                    INSERT OR IGNORE INTO active_heads(run_id, commit_oid, round, status, updated_at)
                    SELECT run_id, commit_oid, 0, 'active', created_at
                    FROM champion_history champion
                    WHERE sequence = (
                        SELECT MAX(sequence) FROM champion_history latest
                        WHERE latest.run_id = champion.run_id);
                    INSERT OR IGNORE INTO active_heads(run_id, commit_oid, round, status, updated_at)
                    SELECT run_id, candidate_oid, 0, 'active', updated_at
                    FROM (
                        SELECT experiments.*,
                               ROW_NUMBER() OVER (
                                   PARTITION BY run_id ORDER BY sequence DESC, candidate_oid) AS retained_rank
                        FROM experiments
                        WHERE candidate_oid IS NOT NULL
                          AND outcome LIKE 'Rejected%'
                          AND outcome NOT LIKE '%Constraint%'
                          AND outcome NOT LIKE '%Protected%'
                          AND outcome NOT LIKE '%Metric missing%'
                          AND outcome NOT LIKE '%Inconclusive%') retained
                    WHERE retained_rank <= 3;
                    INSERT OR IGNORE INTO experiment_graph_state(
                        run_id, experiment_id, experiment_kind, evaluation_validity,
                        validity_detail, champion_decision, search_status,
                        champion_at_start, hypothesis_family, synthesis_depth, updated_at)
                    SELECT experiments.run_id,
                           experiments.id,
                           CASE WHEN (
                               SELECT COUNT(*) FROM experiment_edges edges
                               WHERE edges.experiment_id = experiments.id) > 1
                               THEN 'synthesis' ELSE 'expansion' END,
                           CASE
                               WHEN experiments.outcome = 'Accepted' THEN 'valid'
                               WHEN experiments.outcome LIKE 'Rejected%'
                                    AND experiments.candidate_oid IS NOT NULL
                                    AND experiments.outcome NOT LIKE '%Constraint%'
                                    AND experiments.outcome NOT LIKE '%Protected%'
                                    AND experiments.outcome NOT LIKE '%Metric missing%'
                                   THEN 'valid'
                               WHEN experiments.outcome LIKE 'Inconclusive%' THEN 'inconclusive'
                               WHEN experiments.outcome = 'Active' THEN 'pending'
                               ELSE 'infrastructure-failed'
                           END,
                           experiments.outcome,
                           CASE WHEN experiments.outcome = 'Accepted'
                                THEN 'promoted' ELSE 'not-promoted' END,
                           CASE WHEN EXISTS(
                               SELECT 1 FROM active_heads heads
                               WHERE heads.run_id = experiments.run_id
                                 AND heads.commit_oid = experiments.candidate_oid)
                                THEN 'active-head'
                                WHEN experiments.outcome = 'Accepted'
                                  OR experiments.outcome LIKE 'Rejected%'
                                THEN 'retained'
                                ELSE 'archived' END,
                           COALESCE(experiments.parent_oid,
                                    json_extract(runs.config_json, '$.baseCommit')),
                           'legacy',
                           CASE WHEN (
                               SELECT COUNT(*) FROM experiment_edges edges
                               WHERE edges.experiment_id = experiments.id) > 1
                               THEN 1 ELSE 0 END,
                           experiments.updated_at
                    FROM experiments
                    JOIN runs ON runs.id = experiments.run_id
                    WHERE COALESCE(experiments.parent_oid,
                                   json_extract(runs.config_json, '$.baseCommit')) IS NOT NULL;
                    CREATE TABLE IF NOT EXISTS knowledge_claim_sources (
                        claim_id TEXT NOT NULL,
                        source_id TEXT NOT NULL,
                        PRIMARY KEY(claim_id, source_id),
                        FOREIGN KEY(claim_id) REFERENCES knowledge_claims(id),
                        FOREIGN KEY(source_id) REFERENCES knowledge_sources(id)
                    );
                    INSERT OR IGNORE INTO knowledge_graph_nodes(
                        project_id, id, kind, canonical_name, attributes_json,
                        version, origin_run_id, created_at)
                    SELECT runs.project_id, 'legacy-entity:' || entities.id, 'entity',
                           entities.canonical_name, entities.attributes_json,
                           1, entities.run_id, runs.created_at
                    FROM knowledge_entities entities
                    JOIN runs ON runs.id = entities.run_id
                    WHERE runs.project_id IS NOT NULL;
                    INSERT OR IGNORE INTO knowledge_graph_nodes(
                        project_id, id, kind, canonical_name, attributes_json,
                        version, origin_run_id, created_at)
                    SELECT runs.project_id, 'legacy-claim:' || claims.id, 'claim',
                           claims.predicate || ': ' || claims.object_value,
                           json_object('predicate', claims.predicate,
                                       'objectKind', claims.object_kind,
                                       'objectValue', claims.object_value),
                           1, claims.run_id, claims.created_at
                    FROM knowledge_claims claims
                    JOIN runs ON runs.id = claims.run_id
                    WHERE runs.project_id IS NOT NULL;
                    INSERT OR IGNORE INTO knowledge_graph_nodes(
                        project_id, id, kind, canonical_name, attributes_json,
                        version, origin_run_id, created_at)
                    SELECT runs.project_id, 'legacy-source:' || sources.id, 'source',
                           sources.location,
                           json_object('location', sources.location, 'kind', sources.kind),
                           1, sources.run_id, sources.captured_at
                    FROM knowledge_sources sources
                    JOIN runs ON runs.id = sources.run_id
                    WHERE runs.project_id IS NOT NULL;
                    INSERT OR IGNORE INTO knowledge_graph_edges(
                        id, project_id, from_id, relation, to_id, confidence,
                        is_inference, rationale, source_ids_json, origin_run_id,
                        valid_from, valid_to)
                    SELECT 'legacy-support:' || claims.id,
                           runs.project_id,
                           'legacy-claim:' || claims.id,
                           'supports',
                           'legacy-entity:' || claims.subject_id,
                           claims.confidence,
                           0, NULL,
                           json_group_array(sources.source_id),
                           claims.run_id, claims.created_at, NULL
                    FROM knowledge_claims claims
                    JOIN runs ON runs.id = claims.run_id
                    JOIN knowledge_claim_sources sources ON sources.claim_id = claims.id
                    WHERE runs.project_id IS NOT NULL
                    GROUP BY claims.id;
                    INSERT OR IGNORE INTO graph_updates(
                        idempotency_key, project_id, run_id, agent_id, created_at)
                    SELECT 'legacy-import:' || runs.id, runs.project_id, runs.id,
                           'migration-v6', CURRENT_TIMESTAMP
                    FROM runs
                    WHERE runs.project_id IS NOT NULL;
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
                    INSERT OR IGNORE INTO schema_migrations(version, applied_at)
                    VALUES (6, CURRENT_TIMESTAMP);
                    UPDATE schema_info SET version = 6;
                    PRAGMA user_version = 6;
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
                   maxInconclusiveRetries = config.Evaluator.MaxInconclusiveRetries
                   maxInfrastructureRetries = config.Evaluator.MaxInfrastructureRetries
                   infrastructureRetryDelaySeconds = config.Evaluator.InfrastructureRetryDelay.TotalSeconds |}
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
               graphSearch =
                {| initialFanOut = config.GraphSearch.InitialFanOut
                   beamWidth = config.GraphSearch.BeamWidth
                   expansionsPerHeadPerRound = config.GraphSearch.ExpansionsPerHeadPerRound
                   ordinaryCandidatesPerSynthesis = config.GraphSearch.OrdinaryCandidatesPerSynthesis
                   stagnationTrigger = config.GraphSearch.StagnationTrigger
                   maxSynthesisBudgetFraction = config.GraphSearch.MaxSynthesisBudgetFraction
                   maxSynthesisDepth = config.GraphSearch.MaxSynthesisDepth
                   conflictResolutionAttempts = config.GraphSearch.ConflictResolutionAttempts
                   maxConflictFiles = config.GraphSearch.MaxConflictFiles
                   maxConflictCharacters = config.GraphSearch.MaxConflictCharacters |}
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

    let private experimentKindText kind =
        match kind with
        | ExperimentKind.Expansion -> "expansion"
        | ExperimentKind.Synthesis -> "synthesis"

    let private parentRoleText role =
        match role with
        | ExperimentParentRole.Primary -> "primary"
        | ExperimentParentRole.Contributor -> "contributor"

    let private evaluationValidityText validity =
        match validity with
        | EvaluationValidity.Pending -> "pending", None
        | EvaluationValidity.Valid -> "valid", None
        | EvaluationValidity.ConstraintFailed constraints -> "constraint-failed", Some(String.concat "," constraints)
        | EvaluationValidity.Inconclusive reason -> "inconclusive", Some reason
        | EvaluationValidity.InfrastructureFailed reason -> "infrastructure-failed", Some reason

    let private championDecisionText decision =
        match decision with
        | ChampionDecision.Pending -> "pending"
        | ChampionDecision.Promoted -> "promoted"
        | ChampionDecision.NotPromoted -> "not-promoted"
        | ChampionDecision.RejectedByUser -> "rejected-by-user"

    let private searchStatusText status =
        match status with
        | SearchStatus.ActiveHead -> "active-head"
        | SearchStatus.Retained -> "retained"
        | SearchStatus.Exhausted -> "exhausted"
        | SearchStatus.ConflictBlocked -> "conflict-blocked"
        | SearchStatus.Archived -> "archived"

    let beginGraphExperiment
        store
        runId
        experimentId
        sequence
        kind
        (parents: ExperimentParent list)
        championAtStart
        outcome
        (_: CancellationToken)
        =
        async {
            try
                let primary =
                    parents
                    |> List.tryFind (fun parent -> parent.Role = ExperimentParentRole.Primary)

                match primary with
                | None ->
                    return
                        Error(
                            HarnessError.create
                                "sqlite.graph_experiment_primary_missing"
                                HarnessErrorCategory.Persistence
                                "A graph experiment requires one primary parent."
                        )
                | Some primaryParent ->
                    use database = connection store
                    database.Open()
                    use transaction = database.BeginTransaction()
                    let timestamp = DateTimeOffset.UtcNow.ToString("o")
                    use experimentCommand = database.CreateCommand()
                    experimentCommand.Transaction <- transaction

                    experimentCommand.CommandText <-
                        """
                        INSERT INTO experiments(id, run_id, sequence, parent_oid, candidate_oid, outcome, created_at, updated_at)
                        VALUES ($id, $run, $sequence, $parent, NULL, $outcome, $created, $updated)
                        ON CONFLICT(id) DO UPDATE SET
                            sequence = excluded.sequence,
                            parent_oid = excluded.parent_oid,
                            outcome = excluded.outcome,
                            updated_at = excluded.updated_at;
                        """

                    addParameter experimentCommand "$id" (ExperimentId.text experimentId)
                    addParameter experimentCommand "$run" (RunId.text runId)
                    addParameter experimentCommand "$sequence" sequence
                    addParameter experimentCommand "$parent" (CommitOid.value primaryParent.Commit)
                    addParameter experimentCommand "$outcome" outcome
                    addParameter experimentCommand "$created" timestamp
                    addParameter experimentCommand "$updated" timestamp
                    experimentCommand.ExecuteNonQuery() |> ignore

                    use stateCommand = database.CreateCommand()
                    stateCommand.Transaction <- transaction

                    stateCommand.CommandText <-
                        """
                        INSERT INTO experiment_graph_state(
                            run_id, experiment_id, experiment_kind, evaluation_validity,
                            validity_detail, champion_decision, search_status,
                            champion_at_start, hypothesis_family, synthesis_depth, updated_at)
                        VALUES ($run, $experiment, $kind, 'pending', NULL, 'pending', 'archived',
                                $champion, 'pending', 0, $updated)
                        ON CONFLICT(experiment_id) DO UPDATE SET
                            experiment_kind = excluded.experiment_kind,
                            champion_at_start = excluded.champion_at_start,
                            updated_at = excluded.updated_at;
                        """

                    addParameter stateCommand "$run" (RunId.text runId)
                    addParameter stateCommand "$experiment" (ExperimentId.text experimentId)
                    addParameter stateCommand "$kind" (experimentKindText kind)
                    addParameter stateCommand "$champion" (CommitOid.value championAtStart)
                    addParameter stateCommand "$updated" timestamp
                    stateCommand.ExecuteNonQuery() |> ignore

                    for parent in parents do
                        use edgeCommand = database.CreateCommand()
                        edgeCommand.Transaction <- transaction

                        edgeCommand.CommandText <-
                            """
                            INSERT INTO experiment_edges(
                                id, run_id, experiment_id, parent_oid, child_oid,
                                edge_kind, parent_role, created_at)
                            VALUES ($edge, $run, $experiment, $parent, NULL, $kind, $role, $created)
                            ON CONFLICT(run_id, experiment_id, parent_oid, parent_role) DO NOTHING;
                            """

                        let edgeId =
                            $"{RunId.text runId}:{ExperimentId.text experimentId}:{parentRoleText parent.Role}:{CommitOid.value parent.Commit}"

                        addParameter edgeCommand "$edge" edgeId
                        addParameter edgeCommand "$run" (RunId.text runId)
                        addParameter edgeCommand "$experiment" (ExperimentId.text experimentId)
                        addParameter edgeCommand "$parent" (CommitOid.value parent.Commit)
                        addParameter edgeCommand "$kind" (experimentKindText kind)
                        addParameter edgeCommand "$role" (parentRoleText parent.Role)
                        addParameter edgeCommand "$created" timestamp
                        edgeCommand.ExecuteNonQuery() |> ignore

                    transaction.Commit()
                    return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.graph_experiment_begin_failed"
                            "Could not persist the planned graph experiment."
                            exceptionValue
                    )
        }

    let saveExperimentGraphState
        store
        runId
        experimentId
        kind
        validity
        decision
        searchStatus
        championAtStart
        hypothesisFamily
        synthesisDepth
        (_: CancellationToken)
        =
        async {
            try
                let validityText, validityDetail = evaluationValidityText validity
                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    """
                    INSERT INTO experiment_graph_state(
                        run_id, experiment_id, experiment_kind, evaluation_validity,
                        validity_detail, champion_decision, search_status,
                        champion_at_start, hypothesis_family, synthesis_depth, updated_at)
                    VALUES ($run, $experiment, $kind, $validity, $detail, $decision, $status,
                            $champion, $family, $depth, $updated)
                    ON CONFLICT(experiment_id) DO UPDATE SET
                        experiment_kind = excluded.experiment_kind,
                        evaluation_validity = excluded.evaluation_validity,
                        validity_detail = excluded.validity_detail,
                        champion_decision = excluded.champion_decision,
                        search_status = excluded.search_status,
                        champion_at_start = excluded.champion_at_start,
                        hypothesis_family = excluded.hypothesis_family,
                        synthesis_depth = excluded.synthesis_depth,
                        updated_at = excluded.updated_at;
                    """

                addParameter command "$run" (RunId.text runId)
                addParameter command "$experiment" (ExperimentId.text experimentId)
                addParameter command "$kind" (experimentKindText kind)
                addParameter command "$validity" validityText

                addParameter command "$detail" (validityDetail |> Option.map box |> Option.defaultValue DBNull.Value)

                addParameter command "$decision" (championDecisionText decision)
                addParameter command "$status" (searchStatusText searchStatus)
                addParameter command "$champion" (CommitOid.value championAtStart)
                addParameter command "$family" hypothesisFamily
                addParameter command "$depth" synthesisDepth
                addParameter command "$updated" (DateTimeOffset.UtcNow.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.experiment_graph_state_failed"
                            "Could not persist the experiment's independent graph outcome dimensions."
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
                    """
                    BEGIN IMMEDIATE;
                    UPDATE experiments SET candidate_oid = $candidate, updated_at = $updated WHERE id = $id;
                    UPDATE experiment_edges SET child_oid = $candidate WHERE experiment_id = $id;
                    COMMIT;
                    """

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

    let private graphNodeKindText kind =
        match kind with
        | GraphNodeKind.Entity -> "entity"
        | GraphNodeKind.Claim -> "claim"
        | GraphNodeKind.Source -> "source"
        | GraphNodeKind.Artifact -> "artifact"
        | GraphNodeKind.AgentRun -> "agent-run"
        | GraphNodeKind.Evaluation -> "evaluation"
        | GraphNodeKind.Task -> "task"
        | GraphNodeKind.Commit -> "commit"
        | GraphNodeKind.Metric -> "metric"

    let private parseGraphNodeKind value =
        match value with
        | "claim" -> GraphNodeKind.Claim
        | "source" -> GraphNodeKind.Source
        | "artifact" -> GraphNodeKind.Artifact
        | "agent-run" -> GraphNodeKind.AgentRun
        | "evaluation" -> GraphNodeKind.Evaluation
        | "task" -> GraphNodeKind.Task
        | "commit" -> GraphNodeKind.Commit
        | "metric" -> GraphNodeKind.Metric
        | _ -> GraphNodeKind.Entity

    let private graphRelationText relation =
        match relation with
        | GraphRelationKind.Mentions -> "mentions"
        | GraphRelationKind.Supports -> "supports"
        | GraphRelationKind.Contradicts -> "contradicts"
        | GraphRelationKind.DerivedFrom -> "derived-from"
        | GraphRelationKind.Produced -> "produced"
        | GraphRelationKind.Evaluates -> "evaluates"
        | GraphRelationKind.Revises -> "revises"
        | GraphRelationKind.Supersedes -> "supersedes"
        | GraphRelationKind.DependsOn -> "depends-on"
        | GraphRelationKind.ParentOf -> "parent-of"
        | GraphRelationKind.ResolvedTo -> "resolved-to"

    let private parseGraphRelation value =
        match value with
        | "supports" -> GraphRelationKind.Supports
        | "contradicts" -> GraphRelationKind.Contradicts
        | "derived-from" -> GraphRelationKind.DerivedFrom
        | "produced" -> GraphRelationKind.Produced
        | "evaluates" -> GraphRelationKind.Evaluates
        | "revises" -> GraphRelationKind.Revises
        | "supersedes" -> GraphRelationKind.Supersedes
        | "depends-on" -> GraphRelationKind.DependsOn
        | "parent-of" -> GraphRelationKind.ParentOf
        | "resolved-to" -> GraphRelationKind.ResolvedTo
        | _ -> GraphRelationKind.Mentions

    let projectIdForRun store runId : Result<string, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()
            command.CommandText <- "SELECT project_id FROM runs WHERE id = $run;"
            addParameter command "$run" (RunId.text runId)

            match command.ExecuteScalar() with
            | :? string as projectId when not (String.IsNullOrWhiteSpace projectId) -> Ok projectId
            | _ ->
                Error(
                    HarnessError.create
                        "sqlite.project_missing"
                        HarnessErrorCategory.Persistence
                        "The run is not linked to a repository project."
                )
        with exceptionValue ->
            Error(persistenceError "sqlite.project_load_failed" "Could not resolve repository identity." exceptionValue)

    let saveRepositoryGraphUpdate store (update: GraphUpdate) (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use transaction = database.BeginTransaction()
                use updateCommand = database.CreateCommand()
                updateCommand.Transaction <- transaction

                updateCommand.CommandText <-
                    """
                    INSERT OR IGNORE INTO graph_updates(idempotency_key, project_id, run_id, agent_id, created_at)
                    VALUES ($key, $project, $run, $agent, $created);
                    SELECT changes();
                    """

                addParameter updateCommand "$key" update.IdempotencyKey
                addParameter updateCommand "$project" update.ProjectId
                addParameter updateCommand "$run" (RunId.text update.RunId)
                addParameter updateCommand "$agent" update.AgentId
                addParameter updateCommand "$created" (DateTimeOffset.UtcNow.ToString("o"))
                let inserted = updateCommand.ExecuteScalar() |> Convert.ToInt32

                if inserted > 0 then
                    for node in update.Nodes do
                        use nodeCommand = database.CreateCommand()
                        nodeCommand.Transaction <- transaction

                        nodeCommand.CommandText <-
                            """
                            INSERT OR IGNORE INTO knowledge_graph_nodes(
                                project_id, id, kind, canonical_name, attributes_json,
                                version, origin_run_id, created_at)
                            VALUES ($project, $id, $kind, $name, $attributes, $version, $run, $created);
                            """

                        addParameter nodeCommand "$project" update.ProjectId
                        addParameter nodeCommand "$id" (GraphNodeId.value node.Id)
                        addParameter nodeCommand "$kind" (graphNodeKindText node.Kind)
                        addParameter nodeCommand "$name" node.CanonicalName

                        addParameter
                            nodeCommand
                            "$attributes"
                            (JsonSerializer.Serialize(node.Attributes |> Map.toArray |> dict))

                        addParameter nodeCommand "$version" node.Version
                        addParameter nodeCommand "$run" (RunId.text node.OriginRunId)
                        addParameter nodeCommand "$created" (node.CreatedAt.ToString("o"))
                        nodeCommand.ExecuteNonQuery() |> ignore

                    for edge in update.Edges do
                        let isInference, rationale, sourceIds =
                            match edge.Provenance with
                            | GraphProvenance.Sourced sources ->
                                0, DBNull.Value :> obj, sources |> Set.toList |> List.map KnowledgeSourceId.text
                            | GraphProvenance.Inference reason -> 1, box reason, []

                        use edgeCommand = database.CreateCommand()
                        edgeCommand.Transaction <- transaction

                        edgeCommand.CommandText <-
                            """
                            INSERT INTO knowledge_graph_edges(
                                id, project_id, from_id, relation, to_id, confidence,
                                is_inference, rationale, source_ids_json, origin_run_id,
                                valid_from, valid_to)
                            VALUES ($id, $project, $from, $relation, $to, $confidence,
                                    $inference, $rationale, $sources, $run, $fromTime, $toTime);
                            """

                        addParameter edgeCommand "$id" (GraphEdgeId.value edge.Id)
                        addParameter edgeCommand "$project" update.ProjectId
                        addParameter edgeCommand "$from" (GraphNodeId.value edge.From)
                        addParameter edgeCommand "$relation" (graphRelationText edge.Relation)
                        addParameter edgeCommand "$to" (GraphNodeId.value edge.To)
                        addParameter edgeCommand "$confidence" (edge.Confidence.ToString(CultureInfo.InvariantCulture))
                        addParameter edgeCommand "$inference" isInference
                        addParameter edgeCommand "$rationale" rationale
                        addParameter edgeCommand "$sources" (JsonSerializer.Serialize sourceIds)
                        addParameter edgeCommand "$run" (RunId.text edge.OriginRunId)
                        addParameter edgeCommand "$fromTime" (edge.ValidFrom.ToString("o"))

                        addParameter
                            edgeCommand
                            "$toTime"
                            (edge.ValidTo
                             |> Option.map (fun value -> box (value.ToString("o")))
                             |> Option.defaultValue DBNull.Value)

                        edgeCommand.ExecuteNonQuery() |> ignore

                transaction.Commit()
                return Ok(inserted > 0)
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.repository_graph_update_failed"
                            "Could not atomically save the repository knowledge update."
                            exceptionValue
                    )
        }

    let loadRepositoryKnowledgeGraph store projectId : Result<RepositoryKnowledgeGraph, HarnessError> =
        try
            let parseAttributes (json: string) =
                use document = JsonDocument.Parse json

                document.RootElement.EnumerateObject()
                |> Seq.map (fun (property: JsonProperty) -> property.Name, property.Value.GetString())
                |> Map.ofSeq

            use database = connection store
            database.Open()
            use nodeCommand = database.CreateCommand()

            nodeCommand.CommandText <-
                """
                SELECT id, kind, canonical_name, attributes_json, version, origin_run_id, created_at
                FROM knowledge_graph_nodes WHERE project_id = $project ORDER BY id, version;
                """

            addParameter nodeCommand "$project" projectId
            use nodeReader = nodeCommand.ExecuteReader()
            let mutable nodes = Map.empty

            while nodeReader.Read() do
                let attributes = parseAttributes (nodeReader.GetString 3)

                let node =
                    { Id = GraphNodeId.create (nodeReader.GetString 0)
                      Kind = parseGraphNodeKind (nodeReader.GetString 1)
                      CanonicalName = nodeReader.GetString 2
                      Attributes = attributes
                      Version = nodeReader.GetInt32 4
                      OriginRunId = RunId.ofGuid (Guid.Parse(nodeReader.GetString 5))
                      CreatedAt = DateTimeOffset.Parse(nodeReader.GetString 6) }

                nodes <-
                    nodes
                    |> Map.change node.Id (fun versions -> node :: Option.defaultValue [] versions |> Some)

            nodeReader.Close()
            use edgeCommand = database.CreateCommand()

            edgeCommand.CommandText <-
                """
                SELECT id, from_id, relation, to_id, confidence, is_inference,
                       rationale, source_ids_json, origin_run_id, valid_from, valid_to
                FROM knowledge_graph_edges WHERE project_id = $project;
                """

            addParameter edgeCommand "$project" projectId
            use edgeReader = edgeCommand.ExecuteReader()
            let mutable edges = Map.empty

            while edgeReader.Read() do
                let provenance =
                    if edgeReader.GetInt32 5 = 1 then
                        GraphProvenance.Inference(
                            if edgeReader.IsDBNull 6 then
                                "legacy inference"
                            else
                                edgeReader.GetString 6
                        )
                    else
                        edgeReader.GetString 7
                        |> JsonSerializer.Deserialize<string list>
                        |> List.map (Guid.Parse >> KnowledgeSourceId.ofGuid)
                        |> Set.ofList
                        |> GraphProvenance.Sourced

                let edge =
                    { Id = GraphEdgeId.create (edgeReader.GetString 0)
                      From = GraphNodeId.create (edgeReader.GetString 1)
                      Relation = parseGraphRelation (edgeReader.GetString 2)
                      To = GraphNodeId.create (edgeReader.GetString 3)
                      Confidence = Decimal.Parse(edgeReader.GetString 4, CultureInfo.InvariantCulture)
                      Provenance = provenance
                      OriginRunId = RunId.ofGuid (Guid.Parse(edgeReader.GetString 8))
                      ValidFrom = DateTimeOffset.Parse(edgeReader.GetString 9)
                      ValidTo =
                        if edgeReader.IsDBNull 10 then
                            None
                        else
                            Some(DateTimeOffset.Parse(edgeReader.GetString 10)) }

                edges <- Map.add edge.Id edge edges

            edgeReader.Close()
            use updateCommand = database.CreateCommand()
            updateCommand.CommandText <- "SELECT idempotency_key FROM graph_updates WHERE project_id = $project;"
            addParameter updateCommand "$project" projectId
            use updateReader = updateCommand.ExecuteReader()
            let mutable updates = Set.empty

            while updateReader.Read() do
                updates <- Set.add (updateReader.GetString 0) updates

            Ok
                { ProjectId = projectId
                  Nodes = nodes
                  Edges = edges
                  AppliedUpdates = updates }
        with
        | :? SqliteException as exceptionValue when exceptionValue.SqliteErrorCode = 1 ->
            Ok(RepositoryKnowledgeGraph.empty projectId)
        | exceptionValue ->
            Error(
                persistenceError
                    "sqlite.repository_graph_load_failed"
                    "Could not load repository-scoped knowledge."
                    exceptionValue
            )

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
                        { HypothesisFamily = "legacy"
                          Hypothesis = reader.GetString 3
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
                """
                SELECT e.sequence,
                       e.run_id,
                       e.experiment_id,
                       e.kind,
                       e.payload,
                       e.created_at,
                       u.input_tokens,
                       u.cached_input_tokens,
                       u.output_tokens,
                       u.reasoning_output_tokens
                FROM events e
                LEFT JOIN usage u
                  ON u.run_id = e.run_id
                 AND u.experiment_id = e.experiment_id
                ORDER BY e.sequence DESC
                LIMIT $limit;
                """

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
                      Usage =
                        if reader.IsDBNull 6 then
                            None
                        else
                            Some
                                { InputTokens = reader.GetInt64 6
                                  CachedInputTokens = reader.GetInt64 7
                                  OutputTokens = reader.GetInt64 8
                                  ReasoningOutputTokens = reader.GetInt64 9 }
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
                """
                SELECT e.sequence,
                       e.run_id,
                       e.experiment_id,
                       e.kind,
                       e.payload,
                       e.created_at,
                       u.input_tokens,
                       u.cached_input_tokens,
                       u.output_tokens,
                       u.reasoning_output_tokens
                FROM events e
                LEFT JOIN usage u
                  ON u.run_id = e.run_id
                 AND u.experiment_id = e.experiment_id
                WHERE e.run_id = $run
                ORDER BY e.sequence;
                """

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
                      Usage =
                        if reader.IsDBNull 6 then
                            None
                        else
                            Some
                                { InputTokens = reader.GetInt64 6
                                  CachedInputTokens = reader.GetInt64 7
                                  OutputTokens = reader.GetInt64 8
                                  ReasoningOutputTokens = reader.GetInt64 9 }
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

    let private parseExperimentKind value =
        match value with
        | "synthesis" -> ExperimentKind.Synthesis
        | _ -> ExperimentKind.Expansion

    let private parseParentRole value =
        match value with
        | "contributor" -> ExperimentParentRole.Contributor
        | _ -> ExperimentParentRole.Primary

    let loadExperimentEdges store runId : Result<StoredExperimentEdge list, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                """
                SELECT id, run_id, experiment_id, parent_oid, child_oid,
                       edge_kind, parent_role, created_at
                FROM experiment_edges
                WHERE run_id = $run
                ORDER BY created_at, id;
                """

            addParameter command "$run" (RunId.text runId)
            use reader = command.ExecuteReader()
            let results = ResizeArray<StoredExperimentEdge>()

            while reader.Read() do
                results.Add
                    { Id = reader.GetString 0
                      RunId = RunId.ofGuid (Guid.Parse(reader.GetString 1))
                      ExperimentId = ExperimentId.ofGuid (Guid.Parse(reader.GetString 2))
                      Parent = CommitOid.create (reader.GetString 3)
                      Child = nullableString reader 4 |> Option.map CommitOid.create
                      Kind = parseExperimentKind (reader.GetString 5)
                      Role = parseParentRole (reader.GetString 6)
                      CreatedAt = DateTimeOffset.Parse(reader.GetString 7) }

            Ok(List.ofSeq results)
        with
        | :? SqliteException as exceptionValue when exceptionValue.SqliteErrorCode = 1 -> Ok []
        | exceptionValue ->
            Error(
                persistenceError
                    "sqlite.experiment_edges_load_failed"
                    "Could not load experiment graph edges."
                    exceptionValue
            )

    let loadExperimentGraphStates store runId : Result<StoredExperimentGraphState list, HarnessError> =
        try
            let validity (value: string) (detail: string option) =
                match value with
                | "valid" -> EvaluationValidity.Valid
                | "constraint-failed" ->
                    detail
                    |> Option.map (fun text -> text.Split(',', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)
                    |> Option.defaultValue []
                    |> EvaluationValidity.ConstraintFailed
                | "inconclusive" -> EvaluationValidity.Inconclusive(Option.defaultValue "Inconclusive" detail)
                | "infrastructure-failed" ->
                    EvaluationValidity.InfrastructureFailed(Option.defaultValue "Infrastructure failure" detail)
                | _ -> EvaluationValidity.Pending

            let decision value =
                match value with
                | "promoted" -> ChampionDecision.Promoted
                | "not-promoted" -> ChampionDecision.NotPromoted
                | "rejected-by-user" -> ChampionDecision.RejectedByUser
                | _ -> ChampionDecision.Pending

            let status value =
                match value with
                | "active-head" -> SearchStatus.ActiveHead
                | "retained" -> SearchStatus.Retained
                | "exhausted" -> SearchStatus.Exhausted
                | "conflict-blocked" -> SearchStatus.ConflictBlocked
                | _ -> SearchStatus.Archived

            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                """
                SELECT run_id, experiment_id, experiment_kind, evaluation_validity,
                       validity_detail, champion_decision, search_status,
                       champion_at_start, hypothesis_family, synthesis_depth, updated_at
                FROM experiment_graph_state
                WHERE run_id = $run
                ORDER BY updated_at, experiment_id;
                """

            addParameter command "$run" (RunId.text runId)
            use reader = command.ExecuteReader()
            let results = ResizeArray<StoredExperimentGraphState>()

            while reader.Read() do
                let detail = nullableString reader 4

                results.Add
                    { RunId = RunId.ofGuid (Guid.Parse(reader.GetString 0))
                      ExperimentId = ExperimentId.ofGuid (Guid.Parse(reader.GetString 1))
                      Kind = parseExperimentKind (reader.GetString 2)
                      Validity = validity (reader.GetString 3) detail
                      ChampionDecision = decision (reader.GetString 5)
                      SearchStatus = status (reader.GetString 6)
                      ChampionAtStart = CommitOid.create (reader.GetString 7)
                      HypothesisFamily = reader.GetString 8
                      SynthesisDepth = reader.GetInt32 9
                      UpdatedAt = DateTimeOffset.Parse(reader.GetString 10) }

            Ok(List.ofSeq results)
        with
        | :? SqliteException as exceptionValue when exceptionValue.SqliteErrorCode = 1 -> Ok []
        | exceptionValue ->
            Error(
                persistenceError
                    "sqlite.experiment_graph_states_load_failed"
                    "Could not load independent experiment outcome dimensions."
                    exceptionValue
            )

    let saveChampion store runId sequence commit score experimentId (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    """
                    INSERT INTO champion_history(run_id, sequence, commit_oid, score, experiment_id, created_at)
                    VALUES ($run, $sequence, $commit, $score, $experiment, $created)
                    ON CONFLICT(run_id, sequence) DO NOTHING;
                    """

                addParameter command "$run" (RunId.text runId)
                addParameter command "$sequence" sequence
                addParameter command "$commit" (CommitOid.value commit)

                addParameter
                    command
                    "$score"
                    (score |> Option.map string |> Option.map box |> Option.defaultValue DBNull.Value)

                addParameter
                    command
                    "$experiment"
                    (experimentId
                     |> Option.map ExperimentId.text
                     |> Option.map box
                     |> Option.defaultValue DBNull.Value)

                addParameter command "$created" (DateTimeOffset.UtcNow.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError "sqlite.champion_save_failed" "Could not save champion history." exceptionValue
                    )
        }

    let replaceActiveHeads store runId round heads (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use transaction = database.BeginTransaction()
                use deleteCommand = database.CreateCommand()
                deleteCommand.Transaction <- transaction
                deleteCommand.CommandText <- "DELETE FROM active_heads WHERE run_id = $run;"
                addParameter deleteCommand "$run" (RunId.text runId)
                deleteCommand.ExecuteNonQuery() |> ignore

                for commit in heads do
                    use insertCommand = database.CreateCommand()
                    insertCommand.Transaction <- transaction

                    insertCommand.CommandText <-
                        """
                        INSERT INTO active_heads(run_id, commit_oid, round, status, updated_at)
                        VALUES ($run, $commit, $round, 'active', $updated);
                        """

                    addParameter insertCommand "$run" (RunId.text runId)
                    addParameter insertCommand "$commit" (CommitOid.value commit)
                    addParameter insertCommand "$round" round
                    addParameter insertCommand "$updated" (DateTimeOffset.UtcNow.ToString("o"))
                    insertCommand.ExecuteNonQuery() |> ignore

                transaction.Commit()
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError "sqlite.heads_save_failed" "Could not save active graph heads." exceptionValue
                    )
        }

    let loadActiveHeads store runId : Result<StoredSearchHead list, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                "SELECT run_id, commit_oid, round, status, updated_at FROM active_heads WHERE run_id = $run ORDER BY commit_oid;"

            addParameter command "$run" (RunId.text runId)
            use reader = command.ExecuteReader()
            let results = ResizeArray<StoredSearchHead>()

            while reader.Read() do
                results.Add
                    { RunId = RunId.ofGuid (Guid.Parse(reader.GetString 0))
                      Commit = CommitOid.create (reader.GetString 1)
                      Round = reader.GetInt32 2
                      Status = SearchStatus.ActiveHead
                      UpdatedAt = DateTimeOffset.Parse(reader.GetString 4) }

            Ok(List.ofSeq results)
        with
        | :? SqliteException as exceptionValue when exceptionValue.SqliteErrorCode = 1 -> Ok []
        | exceptionValue ->
            Error(persistenceError "sqlite.heads_load_failed" "Could not load active graph heads." exceptionValue)

    let saveSearchRound store (round: StoredSearchRound) (_: CancellationToken) =
        async {
            try
                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    """
                    INSERT INTO search_rounds(
                        run_id, round, heads_json, completed_heads_json,
                        ordinary_since_synthesis, status, updated_at)
                    VALUES ($run, $round, $heads, $completed, $ordinary, $status, $updated)
                    ON CONFLICT(run_id, round) DO UPDATE SET
                        heads_json = excluded.heads_json,
                        completed_heads_json = excluded.completed_heads_json,
                        ordinary_since_synthesis = excluded.ordinary_since_synthesis,
                        status = excluded.status,
                        updated_at = excluded.updated_at;
                    """

                addParameter command "$run" (RunId.text round.RunId)
                addParameter command "$round" round.Round
                addParameter command "$heads" (round.Heads |> List.map CommitOid.value |> JsonSerializer.Serialize)

                addParameter
                    command
                    "$completed"
                    (round.CompletedHeads
                     |> Set.toList
                     |> List.map CommitOid.value
                     |> JsonSerializer.Serialize)

                addParameter command "$ordinary" round.OrdinarySinceSynthesis
                addParameter command "$status" round.Status
                addParameter command "$updated" (round.UpdatedAt.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return Error(persistenceError "sqlite.round_save_failed" "Could not save search round." exceptionValue)
        }

    let loadLatestSearchRound store runId : Result<StoredSearchRound option, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                """
                SELECT run_id, round, heads_json, completed_heads_json,
                       ordinary_since_synthesis, status, updated_at
                FROM search_rounds
                WHERE run_id = $run
                ORDER BY round DESC LIMIT 1;
                """

            addParameter command "$run" (RunId.text runId)
            use reader = command.ExecuteReader()

            if reader.Read() then
                let commits (json: string) =
                    JsonSerializer.Deserialize<string list>(json) |> List.map CommitOid.create

                Ok(
                    Some
                        { RunId = RunId.ofGuid (Guid.Parse(reader.GetString 0))
                          Round = reader.GetInt32 1
                          Heads = commits (reader.GetString 2)
                          CompletedHeads = commits (reader.GetString 3) |> Set.ofList
                          OrdinarySinceSynthesis = reader.GetInt32 4
                          Status = reader.GetString 5
                          UpdatedAt = DateTimeOffset.Parse(reader.GetString 6) }
                )
            else
                Ok None
        with
        | :? SqliteException as exceptionValue when exceptionValue.SqliteErrorCode = 1 -> Ok None
        | exceptionValue ->
            Error(persistenceError "sqlite.round_load_failed" "Could not load the current search round." exceptionValue)

    let saveSynthesisAttempt store (attempt: StoredSynthesisAttempt) (_: CancellationToken) =
        async {
            try
                let first, second =
                    if
                        StringComparer.Ordinal.Compare(
                            CommitOid.value attempt.Primary,
                            CommitOid.value attempt.Contributor
                        )
                        <= 0
                    then
                        attempt.Primary, attempt.Contributor
                    else
                        attempt.Contributor, attempt.Primary

                use database = connection store
                database.Open()
                use command = database.CreateCommand()

                command.CommandText <-
                    """
                    INSERT INTO synthesis_attempts(
                        run_id, primary_oid, contributor_oid, policy_version,
                        status, experiment_id, detail, updated_at)
                    VALUES ($run, $primary, $contributor, $policy, $status, $experiment, $detail, $updated)
                    ON CONFLICT(run_id, primary_oid, contributor_oid, policy_version) DO UPDATE SET
                        status = excluded.status,
                        experiment_id = excluded.experiment_id,
                        detail = excluded.detail,
                        updated_at = excluded.updated_at;
                    """

                addParameter command "$run" (RunId.text attempt.RunId)
                addParameter command "$primary" (CommitOid.value first)
                addParameter command "$contributor" (CommitOid.value second)
                addParameter command "$policy" attempt.PolicyVersion
                addParameter command "$status" attempt.Status

                addParameter
                    command
                    "$experiment"
                    (attempt.ExperimentId
                     |> Option.map ExperimentId.text
                     |> Option.map box
                     |> Option.defaultValue DBNull.Value)

                addParameter
                    command
                    "$detail"
                    (attempt.FailureDetail |> Option.map box |> Option.defaultValue DBNull.Value)

                addParameter command "$updated" (attempt.UpdatedAt.ToString("o"))
                command.ExecuteNonQuery() |> ignore
                return Ok()
            with exceptionValue ->
                return
                    Error(
                        persistenceError
                            "sqlite.synthesis_attempt_save_failed"
                            "Could not save synthesis attempt."
                            exceptionValue
                    )
        }

    let loadSynthesisAttempts store runId : Result<StoredSynthesisAttempt list, HarnessError> =
        try
            use database = connection store
            database.Open()
            use command = database.CreateCommand()

            command.CommandText <-
                """
                SELECT run_id, primary_oid, contributor_oid, policy_version,
                       status, experiment_id, detail, updated_at
                FROM synthesis_attempts WHERE run_id = $run;
                """

            addParameter command "$run" (RunId.text runId)
            use reader = command.ExecuteReader()
            let results = ResizeArray<StoredSynthesisAttempt>()

            while reader.Read() do
                results.Add
                    { RunId = RunId.ofGuid (Guid.Parse(reader.GetString 0))
                      Primary = CommitOid.create (reader.GetString 1)
                      Contributor = CommitOid.create (reader.GetString 2)
                      PolicyVersion = reader.GetInt32 3
                      Status = reader.GetString 4
                      ExperimentId = nullableString reader 5 |> Option.map (Guid.Parse >> ExperimentId.ofGuid)
                      FailureDetail = nullableString reader 6
                      UpdatedAt = DateTimeOffset.Parse(reader.GetString 7) }

            Ok(List.ofSeq results)
        with
        | :? SqliteException as exceptionValue when exceptionValue.SqliteErrorCode = 1 -> Ok []
        | exceptionValue ->
            Error(
                persistenceError
                    "sqlite.synthesis_attempts_load_failed"
                    "Could not load synthesis attempts."
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
                        { HypothesisFamily = "legacy"
                          Hypothesis = reader.GetString 3
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
