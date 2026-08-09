# FsHarness: Turning the Karpathy Loop into Durable Graph Search

What happens when a coding agent can make a change, measure the result, learn from it, and try again—for hours rather than for one prompt?

That is the problem behind [FsHarness](https://github.com/fwaris/FsHarness), an F#/.NET system for running measured, reversible Codex engineering experiments.

## The problem: *Unstructured agent experimentation fragments the codebase.*

Coding agents are good at producing plausible changes. The harder problem is running a long sequence of experiments safely and being able to answer:

- Did the change actually improve the target metric?
- Can we reproduce or reverse it?
- What did earlier failures teach us?
- What dead or redundant code still remains?

A chat transcript is not an experiment database. We need an explicit objective, a protected code zone, a trusted evaluator, resource budgets, persisted lineage, and specific rules for deciding what happens next.

## The Karpathy loop

Andrej Karpathy's `autoresearch` popularized a beautifully small pattern:

1. Inspect the current program and experiment history.
2. Propose and implement one motivated change.
3. Commit the candidate to Git, creating a durable diff, parent, and rollback point.
4. Run a short, fixed evaluation.
5. Keep the change if the metric improves; otherwise revert it.
6. Record the outcome and repeat.

The loop works because progress is **verifiable**, changes are **reversible**, feedback arrives on a **short horizon**, and the environment is **bounded**. It turns an agent from a one-shot code generator into an optimizer with a ratchet.

But the basic ratchet is linear. A candidate that loses today may still contain an idea worth extending tomorrow. Resetting everything to one winner throws away useful search structure.

## From a loop to a graph

The paper included with FsHarness, [*Graph Engineering: The Karpathy Loop, Improved 1000x by Itself - The Anthropic Playbook*](https://github.com/fwaris/FsHarness/blob/main/karpathy_loop_graph.pdf), proposes externalizing more of the research process:

- the loop stores iteration and evaluation;
- a work DAG stores experiment lineage and alternative paths;
- multi-agent workflows add parallel search and specialized roles;
- a knowledge graph stores claims, relationships, provenance, and cross-session memory.

The central idea is that agents should retrieve the small, relevant part of the graph for their task instead of replaying every previous transcript. The commit DAG answers *what changed and what descended from it*; the knowledge graph answers *what we know, how facts relate, and what evidence supports them*.

The implementation has two complementary SQLite-backed views: a run-scoped claim index for lexical search and a repository-scoped, versioned graph for cross-run context. The complete write path, schema, provenance rules, and retrieval behavior are described in [**How the FsHarness SQLite knowledge base works**](https://github.com/fwaris/FsHarness/blob/main/docs/sqlite-knowledge-base.md).

FsHarness applies that idea to Codex experiments. It keeps a global champion plus a bounded beam of independently expandable heads. A new candidate can derive from one head while still being evaluated against the champion. Constraint-valid non-winners remain available for later exploration, and eligible ideas from incomparable heads can be synthesized into a real two-parent Git commit.

The surrounding machinery is deliberately durable: private Git worktrees isolate experiments from the source checkout; SQLite stores events, metrics, plans, graph state, and recovery data; evaluator results follow a typed protocol; and token, time, failure, and experiment budgets bound the campaign. The repository also exposes typed dependency plans and bounded multi-agent execution for custom orchestration.

## Set up and run FsHarness

The core experiment is defined in a `campaign` JSON file. It includes:

- The experiment prompt and optimization objective.
- The Codex model to use (for example, `luna` or `terra`).
- A token budget.
- Other required parameters.

There are two ways to get started. In **manual mode**, launch FsHarness with `dotnet`; use the desktop editor to configure and run the campaign. In **Codex mode**, clone FsHarness, add it to a Codex *project*, and ask Codex to create, validate, launch, monitor, and recover a campaign for your particular repo and objective.

The complete prerequisites, commands, campaign fields, evaluator contract, Windows path guidance, and a ready-to-use Codex prompt are in [**Set up and run FsHarness**](https://github.com/fwaris/FsHarness/blob/main/docs/setup-and-run.md).

## Codex `/goal` as the meta-loop

Codex has a [`/goal`](https://learn.chatgpt.com/use-cases/follow-goals) command. It can be used to autonomously drive Codex toward a goal. Codex iterates for a long time to try to achieve the goal (or until a stopping condition is met).

This provides a useful second layer above FsHarness and a natural **meta-loop** for operating a long FsHarness campaign.

When using the Codex CLI internally, FsHarness explicitly disables goals, apps, hooks, subagents, and remote plugins inside each experiment worker. Inner workers stay ephemeral, isolated, and focused on one measured change. The outer Codex goal manages the campaign lifecycle: launch, observe, recover, and summarize.

So the system has two feedback loops:

1. **FsHarness** searches for a better measured candidate.
2. **Codex `/goal`** watches the campaign runs and revises the campaign for subsequent runs, enabling more intelligent autonomous exploration.

## Aid to Tokenmaxers

Many companies are imposing monthly caps on token usage. To extract the most value from the available budget, FsHarness can be configured to do the bulk of the exploration with lower-cost models — for example, `luna` or `terra`— thereby freeing valuable token capacity for deeper analysis and planning-type work.

By querying the rich knowledge graph before generating new candidates, FsHarness can give even lower-cost models enough context to reliably drive the solution search forward.

## Screenshots

The FsHarness repo contains a desktop GUI app. It can be used to manually define and run a campaign or to monitor a campaign launched either manually or through Codex.

The top banner of this post depicts the evolution graph; other views are shown below:

### Setup

![settings](/images/setup.png)

### History Log

![history log](/images/log.png)

### Knowledge Entries

![knowledge entries](/images/knowlege_graph.png)
