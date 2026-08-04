namespace FsHarness.Infrastructure

open System
open System.Threading
open FsHarness.Core
open FSharp.Control

type AgentTask<'value> =
    { Assignment: AgentAssignment
      Execute: CancellationToken -> Async<Result<'value, HarnessError>> }

type AgentTaskResult<'value> =
    { Assignment: AgentAssignment
      Result: Result<'value, HarnessError> }

[<RequireQualifiedAccess>]
module MultiAgent =
    let private unexpectedError assignment (exceptionValue: exn) =
        HarnessError.create
            "agent.unexpected_failure"
            HarnessErrorCategory.Recovery
            $"Agent '{AgentId.value assignment.AgentId}' failed unexpectedly."
        |> HarnessError.withDetail exceptionValue.Message

    let run maxParallelism cancellationToken (tasks: AgentTask<'value> list) =
        async {
            if maxParallelism <= 0 then
                return
                    Error(
                        HarnessError.create
                            "agent.parallelism_invalid"
                            HarnessErrorCategory.Configuration
                            "Agent parallelism must be greater than zero."
                    )
            else
                let! results =
                    tasks
                    |> List.sortBy (fun task -> AgentId.value task.Assignment.AgentId)
                    |> AsyncSeq.ofSeq
                    |> AsyncSeq.mapAsyncParallelThrottled maxParallelism (fun task ->
                        async {
                            try
                                let! result = task.Execute cancellationToken

                                return
                                    { Assignment = task.Assignment
                                      Result = result }
                            with exceptionValue ->
                                return
                                    { Assignment = task.Assignment
                                      Result = Error(unexpectedError task.Assignment exceptionValue) }
                        })
                    |> AsyncSeq.toListAsync

                return
                    results
                    |> List.sortBy (fun result -> AgentId.value result.Assignment.AgentId)
                    |> Ok
        }
