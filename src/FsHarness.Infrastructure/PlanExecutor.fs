namespace FsHarness.Infrastructure

open System
open System.Threading
open FsHarness.Core

type PlanExecutionReport =
    { Plan: WorkPlan
      Schedule: ScheduleSnapshot
      Assignments: AgentAssignment list }

type WorkItemHandler = AgentAssignment -> CancellationToken -> Async<Result<TokenUsage option, HarnessError>>

[<RequireQualifiedAccess>]
module PlanExecutor =
    let private roleFor (item: WorkItem) =
        if Set.contains ToolCapability.RunDeterministicEvaluator item.RequiredTools then
            AgentRole.Evaluator
        elif Set.contains ToolCapability.PromoteFrontier item.RequiredTools then
            AgentRole.Reviewer
        elif Set.contains ToolCapability.QueryKnowledge item.RequiredTools then
            AgentRole.Researcher
        elif
            Set.contains ToolCapability.GenerateWithCodex item.RequiredTools
            || Set.contains ToolCapability.EditRepository item.RequiredTools
        then
            AgentRole.Implementer
        else
            AgentRole.Planner

    let private assignment parent (item: WorkItem) =
        { AgentId = AgentId.create $"agent/{WorkItemId.value item.Id}"
          Role = roleFor item
          WorkItemId = item.Id
          Parent = parent
          Objective = item.Objective }

    let private budgetError (item: WorkItem) used =
        HarnessError.create
            "agent.work_item_token_budget_exceeded"
            HarnessErrorCategory.Budget
            $"Work item '{WorkItemId.value item.Id}' used {used} raw tokens, exceeding its {item.Budget.MaxRawTokens} token budget."

    let run
        maxParallelism
        parent
        availableTools
        rawTokensRemaining
        deadline
        (handler: WorkItemHandler)
        cancellationToken
        plan
        =
        async {
            match WorkPlan.validate plan with
            | _ when maxParallelism <= 0 ->
                return
                    Error(
                        HarnessError.create
                            "plan.parallelism_invalid"
                            HarnessErrorCategory.Configuration
                            "Plan parallelism must be greater than zero."
                    )
            | Error errors ->
                return
                    Error(
                        HarnessError.create
                            "plan.invalid"
                            HarnessErrorCategory.Configuration
                            "The typed work plan is invalid."
                        |> HarnessError.withDetail (String.concat " " errors)
                    )
            | Ok validated ->
                let rec execute snapshot assignments =
                    async {
                        if Scheduler.isTerminal snapshot then
                            return
                                Ok
                                    { Plan = validated
                                      Schedule = snapshot
                                      Assignments = List.rev assignments }
                        else
                            let now = DateTimeOffset.UtcNow

                            let capacity =
                                { MaxParallelism = maxParallelism
                                  RawTokensRemaining = max 0L (rawTokensRemaining - snapshot.RawTokensUsed)
                                  Deadline = deadline
                                  AvailableTools = availableTools }

                            let ready = Scheduler.ready now capacity validated snapshot

                            if List.isEmpty ready then
                                let pending =
                                    snapshot.States
                                    |> Map.toList
                                    |> List.choose (fun (itemId, state) ->
                                        if state = WorkItemState.Pending then Some itemId else None)

                                let reason =
                                    if now >= deadline then
                                        "Plan deadline reached."
                                    elif capacity.RawTokensRemaining <= 0L then
                                        "Plan token budget exhausted."
                                    else
                                        "A dependency failed or a required tool is unavailable."

                                let skipped =
                                    pending
                                    |> List.fold
                                        (fun current itemId ->
                                            current |> Result.bind (Scheduler.skip now itemId reason))
                                        (Ok snapshot)

                                match skipped with
                                | Error detail ->
                                    return
                                        Error(
                                            HarnessError.create
                                                "plan.skip_failed"
                                                HarnessErrorCategory.Recovery
                                                "The plan executor could not close blocked work items."
                                            |> HarnessError.withDetail detail
                                        )
                                | Ok finalSchedule ->
                                    return
                                        Ok
                                            { Plan = validated
                                              Schedule = finalSchedule
                                              Assignments = List.rev assignments }
                            else
                                let started =
                                    ready
                                    |> List.fold
                                        (fun current item -> current |> Result.bind (Scheduler.start now item.Id))
                                        (Ok snapshot)

                                match started with
                                | Error detail ->
                                    return
                                        Error(
                                            HarnessError.create
                                                "plan.start_failed"
                                                HarnessErrorCategory.Recovery
                                                "The plan executor could not start ready work."
                                            |> HarnessError.withDetail detail
                                        )
                                | Ok running ->
                                    let assigned = ready |> List.map (assignment parent)

                                    let tasks =
                                        List.zip ready assigned
                                        |> List.map (fun (item, agentAssignment) ->
                                            { Assignment = agentAssignment
                                              Execute =
                                                fun token ->
                                                    async {
                                                        let rec attempt attemptNumber =
                                                            async {
                                                                use itemCancellation =
                                                                    CancellationTokenSource.CreateLinkedTokenSource
                                                                        token

                                                                itemCancellation.CancelAfter item.Budget.MaxDuration

                                                                let! outcome =
                                                                    handler agentAssignment itemCancellation.Token
                                                                    |> Async.Catch

                                                                match outcome with
                                                                | Choice1Of2(Error error) when
                                                                    error.Retryable
                                                                    && attemptNumber < item.Budget.MaxAttempts
                                                                    ->
                                                                    return! attempt (attemptNumber + 1)
                                                                | Choice1Of2 result -> return result
                                                                | Choice2Of2(:? OperationCanceledException) when
                                                                    not token.IsCancellationRequested
                                                                    ->
                                                                    return
                                                                        Error(
                                                                            HarnessError.create
                                                                                "agent.work_item_timeout"
                                                                                HarnessErrorCategory.Budget
                                                                                $"Work item '{WorkItemId.value item.Id}' exceeded its duration budget."
                                                                        )
                                                                | Choice2Of2 exceptionValue ->
                                                                    return
                                                                        Error(
                                                                            HarnessError.create
                                                                                "agent.work_item_failure"
                                                                                HarnessErrorCategory.Recovery
                                                                                $"Work item '{WorkItemId.value item.Id}' failed."
                                                                            |> HarnessError.withDetail
                                                                                exceptionValue.Message
                                                                        )
                                                            }

                                                        let! result = attempt 1

                                                        return
                                                            result
                                                            |> Result.bind (fun usage ->
                                                                let used =
                                                                    usage
                                                                    |> Option.map TokenUsage.rawTotal
                                                                    |> Option.defaultValue 0L

                                                                if
                                                                    item.Budget.MaxRawTokens > 0L
                                                                    && used > item.Budget.MaxRawTokens
                                                                then
                                                                    Error(budgetError item used)
                                                                else
                                                                    Ok usage)
                                                    } })

                                    match! MultiAgent.run maxParallelism cancellationToken tasks with
                                    | Error error -> return Error error
                                    | Ok results ->
                                        let completedAt = DateTimeOffset.UtcNow

                                        let completed =
                                            results
                                            |> List.fold
                                                (fun current result ->
                                                    current
                                                    |> Result.bind (fun schedule ->
                                                        match result.Result with
                                                        | Ok usage ->
                                                            Scheduler.succeed
                                                                completedAt
                                                                result.Assignment.WorkItemId
                                                                usage
                                                                schedule
                                                        | Error error ->
                                                            Scheduler.fail
                                                                completedAt
                                                                result.Assignment.WorkItemId
                                                                error
                                                                schedule))
                                                (Ok running)

                                        match completed with
                                        | Error detail ->
                                            return
                                                Error(
                                                    HarnessError.create
                                                        "plan.complete_failed"
                                                        HarnessErrorCategory.Recovery
                                                        "The plan executor could not record completed work."
                                                    |> HarnessError.withDetail detail
                                                )
                                        | Ok next -> return! execute next (List.rev assigned @ assignments)
                    }

                return! execute (WorkPlan.initialSchedule validated) []
        }
