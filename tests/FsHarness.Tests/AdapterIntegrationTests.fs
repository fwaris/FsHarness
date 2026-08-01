namespace FsHarness.Tests

open System
open System.IO
open System.Threading
open FsHarness.Codex
open FsHarness.Core
open FsHarness.Infrastructure
open Xunit

module AdapterFixture =
    let temporaryDirectory () =
        let path =
            Path.Combine(Path.GetTempPath(), "fsharness-adapter-tests", Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory path |> ignore
        path

    let rec private findRepositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "FsHarness.slnx")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate FsHarness.slnx from the test output directory."
        else
            findRepositoryRoot directory.Parent

    let private configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name

    let private executableName name =
        if OperatingSystem.IsWindows() then name + ".exe" else name

    let executable project name =
        let root = findRepositoryRoot (DirectoryInfo AppContext.BaseDirectory)
        Path.Combine(root, "tests", project, "bin", configuration, "net10.0", executableName name)

module SanitizedEnvironmentTests =
    [<Fact>]
    let ``Windows dotnet profile paths survive evaluator sanitization`` () =
        let environment = SanitizedEnvironment.core ()

        if OperatingSystem.IsWindows() then
            Assert.Equal(Environment.GetEnvironmentVariable("APPDATA"), environment["APPDATA"])
            Assert.Equal(Environment.GetEnvironmentVariable("LOCALAPPDATA"), environment["LOCALAPPDATA"])

module AdapterIntegrationTests =
    [<Fact>]
    let ``Codex discovery falls back to the newest VS Code extension`` () =
        let root = AdapterFixture.temporaryDirectory ()

        try
            let executableName = if OperatingSystem.IsWindows() then "codex.exe" else "codex"

            let extensionExecutable version =
                Path.Combine(
                    root,
                    ".vscode",
                    "extensions",
                    $"openai.chatgpt-{version}-darwin-arm64",
                    "bin",
                    "macos-aarch64",
                    executableName
                )

            let older = extensionExecutable "1.9.0"
            let newer = extensionExecutable "1.10.0"
            Directory.CreateDirectory(Path.GetDirectoryName older) |> ignore
            Directory.CreateDirectory(Path.GetDirectoryName newer) |> ignore
            File.WriteAllText(older, "older")
            File.WriteAllText(newer, "newer")

            let resolution = CliDiscovery.resolveFrom None None root

            Assert.Equal(newer, resolution.Executable)
            Assert.Equal(CodexExecutableSource.VisualStudioCodeExtension, resolution.Source)
        finally
            Directory.Delete(root, true)

    [<Fact>]
    let ``Codex discovery preserves override and PATH precedence`` () =
        let root = AdapterFixture.temporaryDirectory ()

        try
            let executableName = if OperatingSystem.IsWindows() then "codex.exe" else "codex"
            let pathDirectory = Path.Combine(root, "path")
            let pathExecutable = Path.Combine(pathDirectory, executableName)

            let extensionExecutable =
                Path.Combine(
                    root,
                    ".vscode",
                    "extensions",
                    "openai.chatgpt-2.0.0-darwin-arm64",
                    "bin",
                    "macos-aarch64",
                    executableName
                )

            Directory.CreateDirectory(pathDirectory) |> ignore
            Directory.CreateDirectory(Path.GetDirectoryName extensionExecutable) |> ignore
            File.WriteAllText(pathExecutable, "path")
            File.WriteAllText(extensionExecutable, "extension")

            let fromPath = CliDiscovery.resolveFrom None (Some pathDirectory) root

            let configured =
                CliDiscovery.resolveFrom (Some "/configured/codex") (Some pathDirectory) root

            Assert.Equal(pathExecutable, fromPath.Executable)
            Assert.Equal(CodexExecutableSource.PathEnvironment, fromPath.Source)
            Assert.Equal("/configured/codex", configured.Executable)
            Assert.Equal(CodexExecutableSource.EnvironmentOverride, configured.Source)
        finally
            Directory.Delete(root, true)

    [<Fact>]
    let ``fake Codex exercises preflight JSONL structured output and usage`` () =
        let root = AdapterFixture.temporaryDirectory ()

        try
            let workspace = Path.Combine(root, "worktree")
            Directory.CreateDirectory(Path.Combine(workspace, "src")) |> ignore
            File.WriteAllText(Path.Combine(workspace, "src", "score.txt"), "1")

            let executable =
                AdapterFixture.executable "FsHarness.FakeCodex" "FsHarness.FakeCodex"

            let preflight =
                Cli.preflight executable CancellationToken.None |> Async.RunSynchronously

            match preflight with
            | Error error -> Assert.Fail error.Summary
            | Ok report ->
                Assert.Equal("codex-cli 0.fake", report.Version)
                Assert.Contains(report.Models, fun model -> model.Id = "gpt-5.6-luna")

            let schemaPath = Path.Combine(root, "schema.json")
            File.WriteAllText(schemaPath, "{}")

            let result =
                Cli.run
                    { Executable = executable
                      WorkingDirectory = workspace
                      Model = Defaults.model
                      Prompt = "Make one deterministic improvement."
                      OutputSchemaPath = schemaPath
                      Timeout = TimeSpan.FromSeconds 10.0
                      JsonlPath = Path.Combine(root, "artifacts", "codex.jsonl")
                      StderrPath = Path.Combine(root, "artifacts", "codex.stderr.log") }
                    CancellationToken.None
                |> Async.RunSynchronously

            match result with
            | Error error -> Assert.Fail error.Summary
            | Ok completed ->
                Assert.Equal("fake-thread-1", completed.ThreadId)
                Assert.Equal(Some 120L, completed.Usage |> Option.map TokenUsage.rawTotal)
                Assert.Equal("Increment the deterministic fixture score.", completed.Summary.Hypothesis)
                Assert.Equal("2", File.ReadAllText(Path.Combine(workspace, "src", "score.txt")))
                Assert.True(File.Exists(Path.Combine(root, "artifacts", "codex.jsonl")))
        finally
            Directory.Delete(root, true)

    [<Fact>]
    let ``fake evaluator obeys result path protocol`` () =
        let root = AdapterFixture.temporaryDirectory ()

        try
            let workspace = Path.Combine(root, "worktree")
            Directory.CreateDirectory(Path.Combine(workspace, "src")) |> ignore
            File.WriteAllText(Path.Combine(workspace, "src", "score.txt"), "12.34")

            let spec =
                { Executable = AdapterFixture.executable "FsHarness.FakeEvaluator" "FsHarness.FakeEvaluator"
                  Arguments = []
                  WorkingDirectory = "."
                  Timeout = TimeSpan.FromSeconds 10.0
                  RequiredConstraints = [ "build"; "tests" ]
                  MaxInconclusiveRetries = 2 }

            let result =
                Evaluator.run
                    spec
                    workspace
                    workspace
                    (Path.Combine(root, "artifacts", "evaluation.json"))
                    CancellationToken.None
                |> Async.RunSynchronously

            match result with
            | Error error -> Assert.Fail error.Summary
            | Ok evaluation ->
                Assert.Equal(12.34M, evaluation.Metrics["primary"])
                Assert.True evaluation.Constraints["build"]
                Assert.True evaluation.Constraints["tests"]
        finally
            Directory.Delete(root, true)
