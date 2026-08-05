namespace FsHarness.App

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FsHarness.Infrastructure

type HeadlessCampaignStatus =
    { ConfigPath: string
      DataRoot: string
      IsRunning: bool
      ProcessId: int option
      StartedAt: DateTimeOffset option
      ActivityLogPath: string option
      SummaryPath: string option }

[<CLIMutable>]
type private CampaignProcessRecord =
    { SchemaVersion: int
      ConfigPath: string
      DataRoot: string
      ProcessId: int
      StartedAt: DateTimeOffset
      ControlPath: string
      ActivityLogPath: string
      SummaryPath: string }

[<RequireQualifiedAccess>]
module HeadlessCampaign =
    let private jsonOptions =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let private campaignKey (configPath: string) =
        let normalized = Path.GetFullPath(configPath)
        let digest = SHA256.HashData(Encoding.UTF8.GetBytes normalized)
        Convert.ToHexString(digest).ToLowerInvariant().Substring(0, 16)

    let private launchDirectory dataRoot configPath =
        Path.Combine(Path.GetFullPath dataRoot, "launches", campaignKey configPath)

    let private recordPath dataRoot configPath =
        Path.Combine(launchDirectory dataRoot configPath, "campaign-process.json")

    let private emptyStatus configPath dataRoot =
        { ConfigPath = Path.GetFullPath configPath
          DataRoot = Path.GetFullPath dataRoot
          IsRunning = false
          ProcessId = None
          StartedAt = None
          ActivityLogPath = None
          SummaryPath = None }

    let private processMatches (record: CampaignProcessRecord) =
        try
            use headlessProcess = Process.GetProcessById record.ProcessId

            not headlessProcess.HasExited
            && abs ((DateTimeOffset(headlessProcess.StartTime) - record.StartedAt).TotalSeconds) < 5.0
        with
        | :? ArgumentException -> false
        | :? InvalidOperationException -> false
        | :? System.ComponentModel.Win32Exception -> false

    let private readRecord dataRoot configPath =
        let path = recordPath dataRoot configPath

        if not (File.Exists path) then
            None
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                let root = document.RootElement

                Some
                    { SchemaVersion = root.GetProperty("schemaVersion").GetInt32()
                      ConfigPath = root.GetProperty("configPath").GetString()
                      DataRoot = root.GetProperty("dataRoot").GetString()
                      ProcessId = root.GetProperty("processId").GetInt32()
                      StartedAt = root.GetProperty("startedAt").GetDateTimeOffset()
                      ControlPath = root.GetProperty("controlPath").GetString()
                      ActivityLogPath = root.GetProperty("activityLogPath").GetString()
                      SummaryPath = root.GetProperty("summaryPath").GetString() }
            with
            | :? IOException
            | :? JsonException -> None

    let status configPath dataRoot =
        try
            match readRecord dataRoot configPath with
            | None -> emptyStatus configPath dataRoot
            | Some record ->
                { ConfigPath = record.ConfigPath
                  DataRoot = record.DataRoot
                  IsRunning = processMatches record
                  ProcessId = Some record.ProcessId
                  StartedAt = Some record.StartedAt
                  ActivityLogPath = Some record.ActivityLogPath
                  SummaryPath = Some record.SummaryPath }
        with _ ->
            { ConfigPath = configPath
              DataRoot = dataRoot
              IsRunning = false
              ProcessId = None
              StartedAt = None
              ActivityLogPath = None
              SummaryPath = None }

    let private configuredCliPath () =
        match Environment.GetEnvironmentVariable "FSHARNESS_CLI_PATH" with
        | value when not (String.IsNullOrWhiteSpace value) -> Some(Path.GetFullPath value)
        | _ -> None

    let private devCliPath () =
        let baseDirectory = DirectoryInfo(AppContext.BaseDirectory)

        let rec findSourceRoot (directory: DirectoryInfo) remaining =
            if isNull directory || remaining = 0 then
                None
            else
                let source = Path.Combine(directory.FullName, "src", "FsHarness.Cli")

                if Directory.Exists source then
                    Some source
                else
                    findSourceRoot directory.Parent (remaining - 1)

        findSourceRoot baseDirectory 8
        |> Option.bind (fun projectDirectory ->
            let candidates =
                Directory.EnumerateFiles(projectDirectory, "FsHarness.Cli.dll", SearchOption.AllDirectories)
                |> Seq.filter (fun path ->
                    path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                |> Seq.sortByDescending File.GetLastWriteTimeUtc
                |> Seq.tryHead

            candidates)

    let private cliInvocation () =
        let localDll = Path.Combine(AppContext.BaseDirectory, "FsHarness.Cli.dll")
        let localExecutable = Path.Combine(AppContext.BaseDirectory, "fsharness")
        let localWindowsExecutable = Path.Combine(AppContext.BaseDirectory, "fsharness.exe")

        let selected =
            configuredCliPath ()
            |> Option.orElseWith (fun () ->
                if File.Exists localExecutable then
                    Some localExecutable
                else
                    None)
            |> Option.orElseWith (fun () ->
                if File.Exists localWindowsExecutable then
                    Some localWindowsExecutable
                else
                    None)
            |> Option.orElseWith (fun () -> if File.Exists localDll then Some localDll else None)
            |> Option.orElseWith devCliPath

        match selected with
        | Some path when String.Equals(Path.GetExtension path, ".dll", StringComparison.OrdinalIgnoreCase) ->
            Ok("dotnet", [ path ])
        | Some path -> Ok(path, [])
        | None ->
            Error "Could not find FsHarness.Cli. Build the CLI or set FSHARNESS_CLI_PATH to its executable or DLL."

    let launch (configPath: string) (dataRoot: string) (codexPath: string) =
        let config = Path.GetFullPath configPath
        let root = Path.GetFullPath dataRoot
        let current = status config root

        if current.IsRunning then
            Error "The loaded campaign already has a running headless process."
        elif not (File.Exists config) then
            Error $"The loaded campaign file does not exist: {config}"
        else
            match cliInvocation () with
            | Error error -> Error error
            | Ok(executable, prefixArguments) ->
                try
                    let directory = launchDirectory root config
                    Directory.CreateDirectory directory |> ignore
                    let controlPath = Path.Combine(directory, "stop.request")
                    let activityPath = Path.Combine(directory, "activity.log")
                    let summaryPath = Path.Combine(directory, "campaign-summary.json")

                    if File.Exists controlPath then
                        File.Delete controlPath

                    let startInfo = ProcessStartInfo()
                    startInfo.FileName <- executable
                    startInfo.WorkingDirectory <- Path.GetDirectoryName config
                    startInfo.UseShellExecute <- false
                    startInfo.CreateNoWindow <- true

                    [ yield! prefixArguments
                      "run"
                      "--config"
                      config
                      "--data-root"
                      root
                      "--codex"
                      codexPath
                      "--summary"
                      summaryPath
                      "--control-file"
                      controlPath
                      "--activity-log"
                      activityPath ]
                    |> List.iter startInfo.ArgumentList.Add

                    use headlessProcess = new Process(StartInfo = startInfo)

                    if not (headlessProcess.Start()) then
                        Error "The headless campaign process could not be started."
                    else
                        let record =
                            { SchemaVersion = 1
                              ConfigPath = config
                              DataRoot = root
                              ProcessId = headlessProcess.Id
                              StartedAt = DateTimeOffset(headlessProcess.StartTime)
                              ControlPath = controlPath
                              ActivityLogPath = activityPath
                              SummaryPath = summaryPath }

                        {| schemaVersion = record.SchemaVersion
                           configPath = record.ConfigPath
                           dataRoot = record.DataRoot
                           processId = record.ProcessId
                           startedAt = record.StartedAt
                           controlPath = record.ControlPath
                           activityLogPath = record.ActivityLogPath
                           summaryPath = record.SummaryPath |}
                        |> fun value -> JsonSerializer.Serialize(value, jsonOptions)
                        |> AtomicFile.writeAllText (recordPath root config)

                        Ok(status config root)
                with exceptionValue ->
                    Error $"Unable to launch the headless campaign: {exceptionValue.Message}"

    let requestStop configPath dataRoot =
        match readRecord dataRoot configPath with
        | None -> Error "No headless process record exists for the loaded campaign."
        | Some record when not (processMatches record) -> Error "The loaded campaign is not currently running."
        | Some record ->
            try
                AtomicFile.writeAllText record.ControlPath $"stop requested at {DateTimeOffset.UtcNow:o}"
                Ok()
            with exceptionValue ->
                Error $"Unable to request campaign stop: {exceptionValue.Message}"
