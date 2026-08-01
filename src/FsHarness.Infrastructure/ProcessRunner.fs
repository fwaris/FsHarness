namespace FsHarness.Infrastructure

open System
open System.Diagnostics
open System.Text
open System.Threading
open System.Threading.Tasks
open FsHarness.Core

type ProcessSpec =
    { Executable: string
      Arguments: string list
      WorkingDirectory: string option
      StandardInput: string option
      Environment: Map<string, string>
      ClearEnvironment: bool
      Timeout: TimeSpan
      MaxCaptureCharacters: int }

type ProcessResult =
    { ExitCode: int
      Stdout: string
      Stderr: string }

[<RequireQualifiedAccess>]
module SanitizedEnvironment =
    let core () =
        [ "PATH"
          "HOME"
          "TMPDIR"
          "TEMP"
          "TMP"
          "DOTNET_ROOT"
          "USERPROFILE"
          "SystemRoot" ]
        |> List.choose (fun name ->
            match Environment.GetEnvironmentVariable name with
            | value when not (String.IsNullOrWhiteSpace value) -> Some(name, value)
            | _ -> None)
        |> Map.ofList

[<RequireQualifiedAccess>]
module ProcessRunner =
    let private error code summary detail retryable =
        let value =
            HarnessError.create code HarnessErrorCategory.Persistence summary
            |> HarnessError.withDetail detail

        if retryable then HarnessError.retryable value else value

    let private truncate limit (value: string) =
        if value.Length <= limit then
            value
        else
            value.Substring(0, limit) + "…"

    let run (spec: ProcessSpec) (cancellationToken: CancellationToken) =
        async {
            use linked = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
            linked.CancelAfter spec.Timeout

            let startInfo = ProcessStartInfo()
            startInfo.FileName <- spec.Executable
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.RedirectStandardInput <- spec.StandardInput.IsSome
            startInfo.CreateNoWindow <- true

            spec.WorkingDirectory
            |> Option.iter (fun path -> startInfo.WorkingDirectory <- path)

            spec.Arguments |> List.iter startInfo.ArgumentList.Add

            if spec.ClearEnvironment then
                startInfo.Environment.Clear()

            spec.Environment
            |> Map.iter (fun name value -> startInfo.Environment[name] <- value)

            use childProcess = new Process(StartInfo = startInfo)

            try
                if not (childProcess.Start()) then
                    return Error(error "process.start_failed" "Process did not start." spec.Executable true)
                else
                    match spec.StandardInput with
                    | Some input ->
                        do!
                            childProcess.StandardInput.WriteAsync(input.AsMemory(), linked.Token)
                            |> Async.AwaitTask

                        childProcess.StandardInput.Close()
                    | None -> ()

                    let stdoutTask = childProcess.StandardOutput.ReadToEndAsync(linked.Token)
                    let stderrTask = childProcess.StandardError.ReadToEndAsync(linked.Token)

                    try
                        do! childProcess.WaitForExitAsync(linked.Token) |> Async.AwaitTask
                        let! stdout = stdoutTask |> Async.AwaitTask
                        let! stderr = stderrTask |> Async.AwaitTask

                        return
                            Ok
                                { ExitCode = childProcess.ExitCode
                                  Stdout = truncate spec.MaxCaptureCharacters stdout
                                  Stderr = truncate spec.MaxCaptureCharacters stderr }
                    with :? OperationCanceledException ->
                        try
                            if not childProcess.HasExited then
                                childProcess.Kill(true)
                        with _ ->
                            ()

                        return
                            Error(
                                error
                                    "process.timeout_or_cancelled"
                                    "Process timed out or was cancelled."
                                    spec.Executable
                                    false
                            )
            with exceptionValue ->
                try
                    if not childProcess.HasExited then
                        childProcess.Kill(true)
                with _ ->
                    ()

                return Error(error "process.failed" "Process execution failed." exceptionValue.Message true)
        }
