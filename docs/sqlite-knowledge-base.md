# How the FsHarness SQLite knowledge base works

FsHarness stores durable campaign state in a single SQLite database at:

```text
<data-root>/fsharness.db
```

The data root comes from `--data-root`, `FSHARNESS_DATA_DIR`, the experiment file, or the platform default, in that order. The database is not a vector store and does not ask an LLM to search old transcripts. It stores typed experiment facts and graph relationships, then performs deterministic lexical search or bounded graph traversal in F#.

## The three durable structures

It is useful to separate three structures that share the database:

| Structure | Scope | Purpose | Main reader |
| --- | --- | --- | --- |
| Run-scoped claim graph | One campaign run | Searchable entities, aliases, claims, and provenance sources | `fsharness knowledge` and `health` |
| Repository knowledge graph | All runs for one source path | Versioned nodes and immutable, provenance-aware relations | Candidate prompt construction and the desktop Knowledge page |
| Work DAG | One campaign run | Git parentage, champion history, active heads, rounds, and synthesis attempts | Evolution view and graph CLI commands |

The work DAG answers, “Which commit descended from which experiment?” The knowledge graph answers, “What did an experiment assert, what evaluated it, and what evidence supports that relationship?” They are connected through commit and experiment identifiers but are not collapsed into one table.

```text
Codex experiment summary + evaluator result + Git parents
                         |
                         +--> run-scoped entity/source/claims
                         |        |
                         |        +--> lexical `knowledge` query
                         |
                         +--> repository-scoped nodes/edges
                                  |
                                  +--> bounded context for later workers
                                  +--> desktop Knowledge view
```

## Database initialization and ownership

The `HarnessRuntime` opens `<data-root>/fsharness.db`. Initialization creates schema version 6 inside an immediate transaction, requests WAL journal mode, and enables foreign-key checks on the initialization connection. SQLite may therefore create `fsharness.db-wal` and `fsharness.db-shm` while the database is active.

The database contains more than knowledge: run configuration, experiments, events, evaluations, token usage, distilled memories, artifacts, durable operations, plans, annotations, and graph-search state are stored alongside the knowledge tables.

Before upgrading an existing version 1-5 database, FsHarness creates `fsharness.db.pre-v6.bak` if that backup does not already exist. Schema creation and migration then run transactionally.

Treat the database as FsHarness-owned state. Read-only inspection is safe, but do not update its tables manually during a campaign.

## Layer 1: the run-scoped claim graph

The run-scoped layer uses five tables:

| Table | Stored data |
| --- | --- |
| `knowledge_entities` | Typed entities with a canonical name and string attributes |
| `knowledge_aliases` | Normalized aliases mapped to entity IDs |
| `knowledge_sources` | Provenance locations, optional SHA-256 hashes, run and experiment IDs |
| `knowledge_claims` | Subject-predicate-object claims, confidence, and optional supersession |
| `knowledge_claim_sources` | Many-to-many links from claims to provenance sources |

Supported entity kinds are repository, file, symbol, experiment, metric, hypothesis, constraint, and concept. A claim object can be another entity, text, a decimal number, or a Boolean flag. Supported source kinds are artifact, evaluation, commit, agent observation, and user statement.

A claim is valid only when:

- its subject exists;
- an entity-valued object exists;
- its predicate is not blank;
- confidence is between `0` and `1`;
- it cites at least one known source;
- any claim it supersedes already exists.

Canonical names are automatically stored as aliases. Alias normalization trims the value and converts it to lowercase.

### What an evaluated candidate records

After a candidate receives a completed, non-inconclusive evaluator result, and before the champion decision is made, FsHarness creates:

1. An `experiment` entity whose attributes contain the experiment ID, primary parent commit, and candidate commit.
2. An `evaluation` source pointing to the evaluator result file. If the file exists, its SHA-256 hash is recorded.
3. A `tested-hypothesis` text claim from the Codex experiment summary with confidence `0.8`.
4. One numeric `metric:<name>` claim for every evaluator metric with confidence `1.0`.

Because recording happens before promotion, completed candidates can contribute knowledge even when they do not beat the champion or fail a required constraint. Infrastructure failures and evaluator results that remain inconclusive do not take this normal ingestion path.

The result file itself remains in managed artifact storage. The knowledge source stores its location and hash, while the `evaluations` table separately stores the parsed evaluator JSON.

### How `fsharness knowledge` searches

The CLI command searches this run-scoped layer:

```powershell
dotnet run --project src/FsHarness.Cli/FsHarness.Cli.fsproj -- knowledge `
  --run-id <guid> `
  --query "scheduler budget" `
  --limit 10 `
  --data-root E:\fsh\r12
```

Search is deterministic and lexical:

1. Split the query on whitespace, lowercase the terms, and remove duplicates.
2. For each claim, combine the subject's canonical name, aliases, predicate, and rendered object value.
3. Add one point for each query term contained in that combined text.
4. Add the claim confidence to the lexical score.
5. Discard claims with no lexical match, unless the query has no terms.
6. Sort by descending score and then by claim ID for stable tie-breaking.
7. Return at most the requested limit, including source locations.

This is substring matching, not stemming, embeddings, fuzzy matching, or semantic reranking. The `health` command also loads this layer and reports its claim count.

## Layer 2: the repository-scoped knowledge graph

The repository graph persists across campaigns for the same project. Its project ID is the full source-repository path captured when the run is saved. Runs using the same full path share knowledge; moving or cloning the repository to another path creates a different project scope.

It uses three primary tables:

| Table | Stored data |
| --- | --- |
| `knowledge_graph_nodes` | Versioned typed nodes, keyed by project, stable ID, and version |
| `knowledge_graph_edges` | Immutable typed relations with confidence, provenance, and validity dates |
| `graph_updates` | Idempotency keys identifying already-applied graph updates |

Node kinds are entity, claim, source, artifact, agent run, evaluation, task, commit, and metric. Relation kinds are `MENTIONS`, `SUPPORTS`, `CONTRADICTS`, `DERIVED_FROM`, `PRODUCED`, `EVALUATES`, `REVISES`, `SUPERSEDES`, `DEPENDS_ON`, `PARENT_OF`, and `RESOLVED_TO`.

Every edge has confidence between `0` and `1` and one of two provenance forms:

- a non-empty set of `KnowledgeSourceId` values; or
- an explicit inference rationale.

Source IDs are serialized on the edge as JSON. In the normal runtime path they refer to the evaluation source saved in the run-scoped layer.

### Stable node identities and versioning

Automatic ingestion uses stable IDs:

| Node | Stable ID pattern | Important attributes |
| --- | --- | --- |
| Agent run | `agent-run:<run-id>` | None currently |
| Task | `task:<run-id>` | Campaign objective |
| Source | `source:<experiment-id>` | Evaluation path |
| Artifact | `artifact:<experiment-id>:evaluation` | Authoring run, artifact version, path |
| Evaluation | `evaluation:<experiment-id>` | Rubric and evaluator status |
| Hypothesis claim | `claim:<experiment-id>:hypothesis` | Hypothesis family |
| Commit | `commit:<git-oid>` | None currently |
| Metric | `metric:<experiment-id>:<metric-name>` | Metric name and value |

For an existing node ID, identical content reuses the current version. Changed kind, name, or attributes creates the next integer version. Readers select the highest version as the current node. Old versions remain addressable in SQLite.

Artifact nodes must contain `authoringRun` and `artifactVersion`. Evaluation nodes must contain a `rubric`. A claim node must participate in at least one edge.

### Relations created for each experiment

The automatic update adds these sourced edges, all with confidence `1.0`:

```text
AgentRun   -PRODUCED->   CandidateCommit
Candidate -DEPENDS_ON-> Task
Evaluation -EVALUATES-> CandidateCommit
Artifact    -SUPPORTS-> Evaluation
Claim       -SUPPORTS-> CandidateCommit
ParentCommit -PARENT_OF-> CandidateCommit
Evaluation  -PRODUCED-> Metric
```

There is one `PARENT_OF` edge for every experiment parent, so a synthesized candidate retains both its primary and contributor ancestry.

The runtime labels the update with agent ID `fsharness-headless` and idempotency key:

```text
experiment-evaluated:<experiment-id>
```

### Validation, atomicity, and failure behavior

Before the normal runtime writes a repository update, the pure graph layer checks project identity, non-empty agent and idempotency IDs, node requirements, edge endpoints, confidence, provenance, connected claims, node-version collisions, and attempts to change an existing edge.

The SQLite write then uses one transaction:

1. Insert the idempotency key with `INSERT OR IGNORE`.
2. If the key was new, insert all node versions and edges.
3. Commit the transaction.

Repeating the same update key is a no-op. Nodes are versioned; edge IDs are immutable once written. This makes recovery safe from duplicate repository-graph updates.

Run-scoped entity, source, or claim persistence failures are published as warnings. A repository-scoped graph load, validation, or atomic-save failure is stricter: FsHarness enters recovery rather than continuing with partially recorded shared state.

## How repository knowledge reaches the next Codex worker

Before preparing a candidate prompt, FsHarness loads the repository graph and seeds a query with:

- `task:<current-run-id>`;
- `commit:<selected-parent-oid>`;
- `commit:<current-champion-oid>`.

The query traverses both incoming and outgoing edges with these limits:

- at most two hops;
- at most 80 edges;
- at most `promptProfile.maxMemoryCharacters` serialized characters;
- only `SUPPORTS`, `CONTRADICTS`, `DERIVED_FROM`, `PRODUCED`, `EVALUATES`, `SUPERSEDES`, `DEPENDS_ON`, `PARENT_OF`, and `RESOLVED_TO` relations;
- contradictory edges included;
- only currently valid edges when no historical `asOf` time is supplied.

Edges are selected in stable edge-ID order. Each selected edge becomes one prompt line:

```text
[<stable-edge-id>] <from-name> -<RELATION>-> <to-name>
```

If the result exceeds an edge or character limit, the prompt says the context was truncated. Edges backed only by inference are listed as uncertain. The worker is instructed to cite stable edge IDs. Node attributes and old transcripts are not dumped into the prompt; the serialized context consists of the selected relationship lines.

This repository context is combined with a separate, bounded list of distilled experiment memories. The `memories` table holds hypothesis, change summary, expected effect, validation notes, and reusable lesson; it is not one of the knowledge-graph tables.

## Desktop view and CLI view are intentionally different

The desktop **Knowledge** page loads the repository-scoped graph for the project associated with a selected run. It shows current node names and all persisted relations with edge ID, provenance type or source count, confidence, and direction. Refresh is manual.

The `fsharness knowledge` CLI command searches only the selected run's claim graph. It does not query the cross-run repository graph. This distinction is important when a desktop view contains knowledge from several campaigns but a CLI search returns only claims from one run.

## Migration of older knowledge

Schema-v6 migration preserves earlier run-scoped knowledge by creating repository nodes with `legacy-entity:`, `legacy-claim:`, and `legacy-source:` prefixes. It creates sourced `legacy-support:` edges and records `legacy-import:<run-id>` idempotency keys. The original run-scoped rows remain in place.

## Current boundaries

- Automatic extraction is deliberately narrow: tested hypotheses and evaluator metrics become claims; FsHarness does not infer a broad ontology from source code or transcripts.
- Repository retrieval is bounded graph traversal, while CLI retrieval is lexical claim search. Neither path uses embeddings.
- Only completed, non-inconclusive candidate evaluations are ingested automatically through the normal campaign path.
- Automatic run-scoped rows use fresh GUIDs and are written in separate operations; only the repository-scoped update has an experiment-level idempotency key and a single transaction.
- The repository graph defines more relation types than automatic experiment ingestion currently emits. Custom orchestration can use the additional types while preserving the same validation rules.
- Project sharing is path-based. The same Git repository at two different filesystem paths has two repository knowledge scopes.
- Temporal fields exist on edges. Automatic experiment edges currently have `ValidTo = None`; the normal query therefore treats them as current.
- SQLite stores durable evidence and references, but it does not decide truth. Evaluator quality, source integrity, metric design, and the Codex experiment summary still determine the quality of recorded knowledge.

For campaign setup and data-root guidance, see [Set up and run FsHarness](setup-and-run.md). For the evaluator result schema and complete operational commands, see the main [README](../README.md).
