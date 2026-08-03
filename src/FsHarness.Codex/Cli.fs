namespace FsHarness.Codex

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open FsHarness.Core

type private CaptureResult =
    { ExitCode: int
      Stdout: string
      Stderr: string }

[<RequireQualifiedAccess>]
type CodexExecutableSource =
    | EnvironmentOverride
    | PathEnvironment
    | VisualStudioCodeExtension
    | ProcessLookup

type CodexExecutableResolution =
    { Executable: string
      Source: CodexExecutableSource }

[<RequireQualifiedAccess>]
module CliDiscovery =
    let private extensionPrefix = "openai.chatgpt-"

    let private executableName =
        if OperatingSystem.IsWindows() then "codex.exe" else "codex"

    let private nonBlank value =
        if String.IsNullOrWhiteSpace value then None else Some value

    let private tryCombine directory fileName =
        try
            Some(Path.Combine(directory, fileName))
        with
        | :? ArgumentException
        | :? NotSupportedException -> None

    let private tryFindOnPath pathValue =
        pathValue
        |> Option.bind nonBlank
        |> Option.bind (fun value ->
            value.Split([| Path.PathSeparator |], StringSplitOptions.RemoveEmptyEntries)
            |> Seq.map (fun entry -> entry.Trim().Trim('"'))
            |> Seq.filter (String.IsNullOrWhiteSpace >> not)
            |> Seq.choose (fun directory -> tryCombine directory executableName)
            |> Seq.tryFind File.Exists)

    let private extensionVersion (extensionDirectory: string) =
        let name = DirectoryInfo(extensionDirectory).Name

        if not (name.StartsWith(extensionPrefix, StringComparison.OrdinalIgnoreCase)) then
            None
        else
            let suffix = name.Substring(extensionPrefix.Length)
            let separator = suffix.IndexOf('-')

            let versionText =
                if separator < 0 then
                    suffix
                else
                    suffix.Substring(0, separator)

            match Version.TryParse versionText with
            | true, version -> Some version
            | false, _ -> None

    let private compareExtensionDirectories left right =
        match extensionVersion left, extensionVersion right with
        | Some leftVersion, Some rightVersion ->
            let byVersion = compare rightVersion leftVersion

            if byVersion <> 0 then
                byVersion
            else
                StringComparer.Ordinal.Compare(right, left)
        | Some _, None -> -1
        | None, Some _ -> 1
        | None, None -> StringComparer.Ordinal.Compare(right, left)

    let private enumerateDirectories path pattern =
        try
            Directory.EnumerateDirectories(path, pattern, SearchOption.TopDirectoryOnly)
            |> Seq.toArray
        with
        | :? DirectoryNotFoundException
        | :? IOException
        | :? UnauthorizedAccessException -> Array.empty

    let private enumerateExecutables binDirectory =
        try
            Directory.EnumerateFiles(binDirectory, executableName, SearchOption.AllDirectories)
            |> Seq.filter (fun path ->
                let relative = Path.GetRelativePath(binDirectory, path)

                let segments =
                    relative.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])

                segments.Length = 2
                && String.Equals(segments[1], executableName, StringComparison.Ordinal))
            |> Seq.sort
            |> Seq.toArray
        with
        | :? DirectoryNotFoundException
        | :? IOException
        | :? UnauthorizedAccessException -> Array.empty

    let private platformScore (path: string) =
        let parent = DirectoryInfo(Path.GetDirectoryName path).Name.ToLowerInvariant()

        let operatingSystemScore =
            if
                OperatingSystem.IsMacOS()
                && parent.StartsWith("macos-", StringComparison.Ordinal)
            then
                2
            elif
                OperatingSystem.IsLinux()
                && parent.StartsWith("linux-", StringComparison.Ordinal)
            then
                2
            elif
                OperatingSystem.IsWindows()
                && parent.StartsWith("windows-", StringComparison.Ordinal)
            then
                2
            else
                0

        let architectureScore =
            match RuntimeInformation.ProcessArchitecture with
            | Architecture.Arm64 when parent.Contains("aarch64", StringComparison.Ordinal) -> 1
            | Architecture.X64 when parent.Contains("x86_64", StringComparison.Ordinal) -> 1
            | _ -> 0

        operatingSystemScore + architectureScore

    let private tryFindInExtensionRoot extensionRoot =
        enumerateDirectories extensionRoot $"{extensionPrefix}*"
        |> Array.sortWith compareExtensionDirectories
        |> Array.tryPick (fun extensionDirectory ->
            Path.Combine(extensionDirectory, "bin")
            |> enumerateExecutables
            |> Array.sortByDescending (fun path -> platformScore path, path)
            |> Array.tryHead)

    let private tryFindVisualStudioCodeExecutable userProfile =
        if String.IsNullOrWhiteSpace userProfile then
            None
        else
            [ Path.Combine(userProfile, ".vscode", "extensions")
              Path.Combine(userProfile, ".vscode-insiders", "extensions") ]
            |> List.tryPick tryFindInExtensionRoot

    let resolveFrom configuredPath pathValue userProfile =
        match configuredPath |> Option.bind nonBlank with
        | Some executable ->
            { Executable = executable
              Source = CodexExecutableSource.EnvironmentOverride }
        | None ->
            match tryFindOnPath pathValue with
            | Some executable ->
                { Executable = executable
                  Source = CodexExecutableSource.PathEnvironment }
            | None ->
                match tryFindVisualStudioCodeExecutable userProfile with
                | Some executable ->
                    { Executable = executable
                      Source = CodexExecutableSource.VisualStudioCodeExtension }
                | None ->
                    { Executable = executableName
                      Source = CodexExecutableSource.ProcessLookup }

    let preferUsable primary fallback canRun =
        if canRun primary.Executable then primary
        elif canRun fallback.Executable then fallback
        else primary

    let private canLaunchVersion executable =
        try
            let startInfo = ProcessStartInfo()
            startInfo.FileName <- executable
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.CreateNoWindow <- true
            startInfo.ArgumentList.Add "--version"

            use processValue = new Process(StartInfo = startInfo)

            if not (processValue.Start()) then
                false
            elif processValue.WaitForExit(5_000) then
                processValue.ExitCode = 0
            else
                try
                    processValue.Kill(true)
                with _ ->
                    ()

                false
        with _ ->
            false

    let resolve configuredPath =
        let pathValue = Environment.GetEnvironmentVariable("PATH") |> Option.ofObj
        let userProfile = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        let primary = resolveFrom configuredPath pathValue userProfile

        match configuredPath with
        | Some _ -> primary
        | None ->
            let extensionFallback = resolveFrom None None userProfile
            preferUsable primary extensionFallback canLaunchVersion

[<RequireQualifiedAccess>]
module Cli =
    let private processError code summary detail retryable =
        let error =
            HarnessError.create code HarnessErrorCategory.Codex summary
            |> HarnessError.withDetail detail

        if retryable then HarnessError.retryable error else error

    let private killProcessTree (childProcess: Process) =
        try
            if not childProcess.HasExited then
                childProcess.Kill(true)
        with _ ->
            ()

    let private runCapture
        (executable: string)
        (arguments: string list)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        =
        task {
            use linked = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
            linked.CancelAfter timeout

            let startInfo = ProcessStartInfo()
            startInfo.FileName <- executable
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.CreateNoWindow <- true

            arguments |> List.iter startInfo.ArgumentList.Add

            use childProcess = new Process(StartInfo = startInfo)

            try
                if not (childProcess.Start()) then
                    return Error(processError "codex.start_failed" "Codex process did not start." executable true)
                else
                    let stdoutTask = childProcess.StandardOutput.ReadToEndAsync(linked.Token)
                    let stderrTask = childProcess.StandardError.ReadToEndAsync(linked.Token)

                    try
                        do! childProcess.WaitForExitAsync(linked.Token)
                        let! stdout = stdoutTask
                        let! stderr = stderrTask

                        return
                            Ok
                                { ExitCode = childProcess.ExitCode
                                  Stdout = stdout
                                  Stderr = stderr }
                    with :? OperationCanceledException ->
                        killProcessTree childProcess

                        return
                            Error(
                                processError
                                    "codex.timeout_or_cancelled"
                                    "Codex command timed out or was cancelled."
                                    (String.concat " " arguments)
                                    true
                            )
            with error ->
                killProcessTree childProcess
                return Error(processError "codex.process_error" "Codex process failed." error.Message true)
        }

    let private requireSuccess (command: string) (result: Result<CaptureResult, HarnessError>) =
        match result with
        | Error error -> Error error
        | Ok value when value.ExitCode = 0 -> Ok value.Stdout
        | Ok value ->
            Error(
                processError
                    "codex.preflight_failed"
                    $"Codex preflight command '{command}' failed with exit code {value.ExitCode}."
                    value.Stderr
                    false
            )

    let preflight (executable: string) (cancellationToken: CancellationToken) =
        async {
            let timeout = TimeSpan.FromSeconds 30.0

            let! version =
                runCapture executable [ "--version" ] timeout cancellationToken
                |> Async.AwaitTask

            match requireSuccess "--version" version with
            | Error error -> return Error error
            | Ok versionText ->
                let! login =
                    runCapture executable [ "login"; "status" ] timeout cancellationToken
                    |> Async.AwaitTask

                match requireSuccess "login status" login with
                | Error error -> return Error error
                | Ok loginText ->
                    let! doctor =
                        runCapture executable [ "doctor"; "--json" ] timeout cancellationToken
                        |> Async.AwaitTask

                    match requireSuccess "doctor --json" doctor with
                    | Error error -> return Error error
                    | Ok doctorJson ->
                        let! catalog =
                            runCapture executable [ "debug"; "models"; "--bundled" ] timeout cancellationToken
                            |> Async.AwaitTask

                        match requireSuccess "debug models --bundled" catalog with
                        | Error error -> return Error error
                        | Ok catalogJson ->
                            match Protocol.parseModelCatalog catalogJson with
                            | Error detail ->
                                return
                                    Error(
                                        processError
                                            "codex.model_catalog_invalid"
                                            "Codex returned an invalid model catalog."
                                            detail
                                            false
                                    )
                            | Ok models ->
                                return
                                    Ok
                                        { Version = versionText.Trim()
                                          LoginStatus = loginText.Trim()
                                          DoctorJson = doctorJson
                                          Models = models }
        }

    let private codexArguments (request: CodexRequest) =
        let writePolicy =
            match Environment.GetEnvironmentVariable "FSHARNESS_CODEX_WRITE_POLICY" with
            | value when String.Equals(value, "unrestricted", StringComparison.OrdinalIgnoreCase) ->
                [ "--dangerously-bypass-approvals-and-sandbox" ]
            | _ -> [ "--sandbox"; "workspace-write" ]

        [ "exec"
          "--json"
          "--color"
          "never"
          "--ephemeral"
          "--ignore-user-config"
          "--strict-config"
          "--model"
          request.Model.Id
          yield! writePolicy
          "-C"
          request.WorkingDirectory
          "--output-schema"
          request.OutputSchemaPath
          "--disable"
          "apps"
          "--disable"
          "hooks"
          "--disable"
          "multi_agent"
          "--disable"
          "goals"
          "--disable"
          "remote_plugin"
          "-c"
          $"model_reasoning_effort=\"{ReasoningEffort.toConfigValue request.Model.Effort}\""
          "-c"
          "approval_policy=\"never\""
          "-c"
          "web_search=\"disabled\""
          "-c"
          "sandbox_workspace_write.network_access=false"
          "-c"
          "allow_login_shell=false"
          "-c"
          "shell_environment_policy.inherit=\"core\""
          "-c"
          "mcp_servers={}"
          "-" ]

    let run (request: CodexRequest) (cancellationToken: CancellationToken) =
        async {
            Directory.CreateDirectory(Path.GetDirectoryName request.JsonlPath) |> ignore
            Directory.CreateDirectory(Path.GetDirectoryName request.StderrPath) |> ignore

            use linked = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
            linked.CancelAfter request.Timeout

            let startInfo = ProcessStartInfo()
            startInfo.FileName <- request.Executable
            startInfo.WorkingDirectory <- request.WorkingDirectory
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardInput <- true
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.CreateNoWindow <- true
            startInfo.Environment["GIT_OPTIONAL_LOCKS"] <- "0"
            startInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
            codexArguments request |> List.iter startInfo.ArgumentList.Add

            use childProcess = new Process(StartInfo = startInfo)

            try
                if not (childProcess.Start()) then
                    return
                        Error(processError "codex.start_failed" "Codex process did not start." request.Executable true)
                else
                    do!
                        childProcess.StandardInput.WriteAsync(request.Prompt.AsMemory(), linked.Token)
                        |> Async.AwaitTask

                    childProcess.StandardInput.Close()

                    use jsonl = new StreamWriter(request.JsonlPath, false, UTF8Encoding(false))
                    use stderr = new StreamWriter(request.StderrPath, false, UTF8Encoding(false))

                    let events = ResizeArray<ProtocolEvent>()

                    let stdoutLines =
                        asyncSeq {
                            let mutable reading = true

                            while reading do
                                let! line =
                                    childProcess.StandardOutput.ReadLineAsync(linked.Token).AsTask()
                                    |> Async.AwaitTask

                                match line with
                                | null -> reading <- false
                                | value -> yield value
                        }

                    let consumeStdout =
                        stdoutLines
                        |> AsyncSeq.iterAsync (fun (line: string) ->
                            async {
                                do! jsonl.WriteLineAsync(line) |> Async.AwaitTask
                                do! jsonl.FlushAsync(linked.Token) |> Async.AwaitTask
                                events.Add(Protocol.parseLine line)
                            })
                        |> fun computation -> Async.StartAsTask(computation, cancellationToken = linked.Token)

                    let consumeStderr =
                        task {
                            let mutable reading = true

                            while reading do
                                let! line = childProcess.StandardError.ReadLineAsync(linked.Token)

                                match line with
                                | null -> reading <- false
                                | value -> do! stderr.WriteLineAsync(value.AsMemory(), linked.Token)

                            do! stderr.FlushAsync(linked.Token)
                        }

                    try
                        do! childProcess.WaitForExitAsync(linked.Token) |> Async.AwaitTask
                        do! consumeStdout |> Async.AwaitTask
                        do! consumeStderr |> Async.AwaitTask

                        let threadId =
                            events
                            |> Seq.choose (function
                                | ThreadStarted id -> Some id
                                | _ -> None)
                            |> Seq.tryHead

                        let usage =
                            events
                            |> Seq.choose (function
                                | TurnCompleted value -> Some value
                                | _ -> None)
                            |> Seq.fold TokenUsage.add TokenUsage.zero
                            |> fun value ->
                                if
                                    events
                                    |> Seq.exists (function
                                        | TurnCompleted _ -> true
                                        | _ -> false)
                                then
                                    Some value
                                else
                                    None

                        let summary =
                            events
                            |> Seq.choose (function
                                | Item item when
                                    item.EventKind = "item.completed" && item.ItemType = Some "agent_message"
                                    ->
                                    item.Text
                                | _ -> None)
                            |> Seq.rev
                            |> Seq.tryPick (fun text ->
                                match Protocol.parseExperimentSummary text with
                                | Ok value -> Some value
                                | Error _ -> None)

                        match childProcess.ExitCode, threadId, summary with
                        | 0, Some id, Some finalSummary ->
                            return
                                Ok
                                    { ThreadId = id
                                      Usage = usage
                                      Summary = finalSummary
                                      ExitCode = childProcess.ExitCode
                                      SawThreadStarted = true }
                        | exitCode, _, _ ->
                            return
                                Error(
                                    processError
                                        "codex.turn_failed"
                                        $"Codex experiment failed with exit code {exitCode}."
                                        $"JSONL: {request.JsonlPath}; stderr: {request.StderrPath}"
                                        (threadId.IsNone)
                                )
                    with :? OperationCanceledException ->
                        killProcessTree childProcess

                        return
                            Error(
                                processError
                                    "codex.timeout_or_cancelled"
                                    "Codex experiment timed out or was cancelled."
                                    request.StderrPath
                                    false
                            )
            with error ->
                killProcessTree childProcess
                return Error(processError "codex.process_error" "Codex process failed." error.Message true)
        }

    let port = { Preflight = preflight; Run = run }
