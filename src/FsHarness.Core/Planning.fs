namespace FsHarness.Core

open System

[<Struct>]
type WorkPlanId = private WorkPlanId of Guid

[<RequireQualifiedAccess>]
module WorkPlanId =
    let create () = WorkPlanId(Guid.NewGuid())
    let ofGuid value = WorkPlanId value
    let value (WorkPlanId value) = value
    let text (WorkPlanId value) = string value

[<Struct>]
type WorkItemId = private WorkItemId of string

[<RequireQualifiedAccess>]
module WorkItemId =
    let create value = WorkItemId value
    let value (WorkItemId value) = value

[<RequireQualifiedAccess>]
type ToolCapability =
    | ReadRepository
    | EditRepository
    | GenerateWithCodex
    | SnapshotWithGit
    | RunDeterministicEvaluator
    | PromoteFrontier
    | QueryKnowledge

type WorkItemBudget =
    { MaxRawTokens: int64
      MaxDuration: TimeSpan
      MaxAttempts: int }

type WorkItem =
    { Id: WorkItemId
      Title: string
      Objective: string
      Dependencies: Set<WorkItemId>
      RequiredTools: Set<ToolCapability>
      Budget: WorkItemBudget
      Priority: int }

type WorkPlan =
    { Id: WorkPlanId
      RunId: RunId
      ExperimentId: ExperimentId
      Objective: string
      Items: Map<WorkItemId, WorkItem>
      CreatedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
type WorkItemState =
    | Pending
    | Running of DateTimeOffset
    | Succeeded of TokenUsage option * DateTimeOffset
    | Failed of HarnessError * DateTimeOffset
    | Skipped of string * DateTimeOffset

type ScheduleSnapshot =
    { States: Map<WorkItemId, WorkItemState>
      RawTokensUsed: int64 }

type SchedulingCapacity =
    { MaxParallelism: int
      RawTokensRemaining: int64
      Deadline: DateTimeOffset
      AvailableTools: Set<ToolCapability> }

[<RequireQualifiedAccess>]
module WorkPlan =
    let private pendingStates plan =
        plan.Items |> Map.map (fun _ _ -> WorkItemState.Pending)

    let initialSchedule plan =
        { States = pendingStates plan
          RawTokensUsed = 0L }

    let private cycleNodes (items: Map<WorkItemId, WorkItem>) =
        let rec visit path visited itemId =
            if Set.contains itemId path then
                Error itemId
            elif Set.contains itemId visited then
                Ok visited
            else
                match Map.tryFind itemId items with
                | None -> Ok(Set.add itemId visited)
                | Some item ->
                    item.Dependencies
                    |> Set.fold
                        (fun result dependency ->
                            result
                            |> Result.bind (fun current -> visit (Set.add itemId path) current dependency))
                        (Ok visited)
                    |> Result.map (Set.add itemId)

        items
        |> Map.keys
        |> Seq.fold
            (fun result itemId -> result |> Result.bind (fun visited -> visit Set.empty visited itemId))
            (Ok Set.empty)
        |> Result.toOption
        |> Option.map (fun _ -> [])
        |> Option.defaultValue [ "Work-item dependencies contain a cycle." ]

    let validate plan =
        let itemIds = plan.Items |> Map.keys |> Set.ofSeq

        let itemErrors =
            plan.Items
            |> Map.values
            |> Seq.collect (fun item ->
                [ if String.IsNullOrWhiteSpace item.Title then
                      $"Work item '{WorkItemId.value item.Id}' has no title."
                  if String.IsNullOrWhiteSpace item.Objective then
                      $"Work item '{WorkItemId.value item.Id}' has no objective."
                  if item.Dependencies |> Set.contains item.Id then
                      $"Work item '{WorkItemId.value item.Id}' depends on itself."
                  for dependency in item.Dependencies do
                      if not (Set.contains dependency itemIds) then
                          $"Work item '{WorkItemId.value item.Id}' has unknown dependency '{WorkItemId.value dependency}'."
                  if item.Budget.MaxRawTokens < 0L then
                      $"Work item '{WorkItemId.value item.Id}' has a negative token budget."
                  if item.Budget.MaxDuration <= TimeSpan.Zero then
                      $"Work item '{WorkItemId.value item.Id}' must have a positive duration budget."
                  if item.Budget.MaxAttempts <= 0 then
                      $"Work item '{WorkItemId.value item.Id}' must allow at least one attempt." ])
            |> List.ofSeq

        let errors = itemErrors @ cycleNodes plan.Items

        if List.isEmpty errors then Ok plan else Error errors

    let standardExperiment runId experimentId objective (budgets: RunBudgets) evaluatorTimeout now =
        let id suffix =
            WorkItemId.create $"{ExperimentId.text experimentId}/{suffix}"

        let generateId = id "generate"
        let snapshotId = id "snapshot"
        let evaluateId = id "evaluate"
        let decideId = id "decide"

        let tokensPerExperiment =
            budgets.MaxRawTokens / int64 (max 1 budgets.MaxExperiments) |> max 1L

        let item itemId title itemObjective dependencies tools budget priority =
            itemId,
            { Id = itemId
              Title = title
              Objective = itemObjective
              Dependencies = Set.ofList dependencies
              RequiredTools = Set.ofList tools
              Budget = budget
              Priority = priority }

        let nonTokenBudget duration =
            { MaxRawTokens = 0L
              MaxDuration = duration
              MaxAttempts = 1 }

        { Id = WorkPlanId.create ()
          RunId = runId
          ExperimentId = experimentId
          Objective = objective
          Items =
            [ item
                  generateId
                  "Generate candidate"
                  objective
                  []
                  [ ToolCapability.ReadRepository
                    ToolCapability.EditRepository
                    ToolCapability.GenerateWithCodex ]
                  { MaxRawTokens = tokensPerExperiment
                    MaxDuration = budgets.CodexTimeout
                    MaxAttempts = 1 }
                  100
              item
                  snapshotId
                  "Freeze candidate"
                  "Capture editable changes as an immutable candidate commit."
                  [ generateId ]
                  [ ToolCapability.SnapshotWithGit ]
                  (nonTokenBudget (TimeSpan.FromMinutes 3.0))
                  90
              item
                  evaluateId
                  "Evaluate candidate"
                  "Run the deterministic evaluator against candidate and parent."
                  [ snapshotId ]
                  [ ToolCapability.RunDeterministicEvaluator ]
                  (nonTokenBudget evaluatorTimeout)
                  80
              item
                  decideId
                  "Decide frontier"
                  "Apply strict promotion policy with compare-and-swap frontier advancement."
                  [ evaluateId ]
                  [ ToolCapability.PromoteFrontier ]
                  (nonTokenBudget (TimeSpan.FromMinutes 3.0))
                  70 ]
            |> Map.ofList
          CreatedAt = now }

[<RequireQualifiedAccess>]
module Scheduler =
    let private succeeded state =
        match state with
        | WorkItemState.Succeeded _ -> true
        | _ -> false

    let ready now capacity plan snapshot =
        let running =
            snapshot.States
            |> Map.values
            |> Seq.filter (function
                | WorkItemState.Running _ -> true
                | _ -> false)
            |> Seq.length

        if
            capacity.MaxParallelism <= running
            || capacity.RawTokensRemaining <= 0L
            || now >= capacity.Deadline
        then
            []
        else
            let available = capacity.MaxParallelism - running

            plan.Items
            |> Map.values
            |> Seq.filter (fun item ->
                Map.tryFind item.Id snapshot.States = Some WorkItemState.Pending
                && item.Dependencies
                   |> Set.forall (fun dependency ->
                       snapshot.States |> Map.tryFind dependency |> Option.exists succeeded)
                && item.Budget.MaxRawTokens <= capacity.RawTokensRemaining
                && Set.isSubset item.RequiredTools capacity.AvailableTools)
            |> Seq.sortBy (fun item -> -item.Priority, WorkItemId.value item.Id)
            |> Seq.truncate available
            |> List.ofSeq

    let start now itemId snapshot =
        match Map.tryFind itemId snapshot.States with
        | Some WorkItemState.Pending ->
            Ok
                { snapshot with
                    States = snapshot.States |> Map.add itemId (WorkItemState.Running now) }
        | _ -> Error $"Work item '{WorkItemId.value itemId}' is not pending."

    let succeed now itemId usage snapshot =
        match Map.tryFind itemId snapshot.States with
        | Some(WorkItemState.Running _) ->
            let used = usage |> Option.map TokenUsage.rawTotal |> Option.defaultValue 0L

            Ok
                { States = snapshot.States |> Map.add itemId (WorkItemState.Succeeded(usage, now))
                  RawTokensUsed = snapshot.RawTokensUsed + used }
        | _ -> Error $"Work item '{WorkItemId.value itemId}' is not running."

    let fail now itemId error snapshot =
        match Map.tryFind itemId snapshot.States with
        | Some(WorkItemState.Running _) ->
            Ok
                { snapshot with
                    States = snapshot.States |> Map.add itemId (WorkItemState.Failed(error, now)) }
        | _ -> Error $"Work item '{WorkItemId.value itemId}' is not running."

    let skip now itemId reason snapshot =
        match Map.tryFind itemId snapshot.States with
        | Some WorkItemState.Pending ->
            Ok
                { snapshot with
                    States = snapshot.States |> Map.add itemId (WorkItemState.Skipped(reason, now)) }
        | _ -> Error $"Work item '{WorkItemId.value itemId}' is not pending."

    let isTerminal snapshot =
        snapshot.States
        |> Map.values
        |> Seq.forall (function
            | WorkItemState.Succeeded _
            | WorkItemState.Failed _
            | WorkItemState.Skipped _ -> true
            | WorkItemState.Pending
            | WorkItemState.Running _ -> false)
