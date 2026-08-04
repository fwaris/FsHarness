namespace FsHarness.App

open System
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Elmish
open Avalonia.FuncUI.Hosts
open Avalonia.Platform.Storage
open Avalonia.Themes.Fluent
open Elmish
open FsHarness.Codex
open FsHarness.Infrastructure

type MainWindow() as this =
    inherit HostWindow()

    let configuredCodexPath =
        match Environment.GetEnvironmentVariable "FSHARNESS_CODEX_PATH" with
        | value when not (String.IsNullOrWhiteSpace value) -> Some value
        | _ -> None

    let codexExecutable = (CliDiscovery.resolve configuredCodexPath).Executable

    let runtime = new HarnessRuntime(codexExecutable)

    let pickRepositoryFolder () =
        async {
            try
                let options =
                    FolderPickerOpenOptions(Title = "Select a source repository", AllowMultiple = false)

                let! folders = this.StorageProvider.OpenFolderPickerAsync options |> Async.AwaitTask

                return folders |> Seq.tryHead |> Option.map (fun folder -> folder.Path.LocalPath) |> Ok
            with error ->
                return Error $"Unable to open the repository folder picker: {error.Message}"
        }

    let pickDataRootFolder () =
        async {
            try
                let options =
                    FolderPickerOpenOptions(Title = "Select the FsHarness data root", AllowMultiple = false)

                let! folders = this.StorageProvider.OpenFolderPickerAsync options |> Async.AwaitTask

                return folders |> Seq.tryHead |> Option.map (fun folder -> folder.Path.LocalPath) |> Ok
            with error ->
                return Error $"Unable to open the data-root folder picker: {error.Message}"
        }

    let experimentFileType () =
        let fileType = FilePickerFileType("FsHarness experiment")
        fileType.Patterns <- [ "*.json" ]
        fileType

    let loadExperiment () =
        async {
            try
                let options =
                    FilePickerOpenOptions(Title = "Load an FsHarness experiment", AllowMultiple = false)

                options.FileTypeFilter <- [ experimentFileType () ]
                let! files = this.StorageProvider.OpenFilePickerAsync options |> Async.AwaitTask

                match files |> Seq.tryHead with
                | None -> return Ok None
                | Some file ->
                    let path = file.Path.LocalPath

                    return
                        match ConfigFile.read path with
                        | Ok config -> Ok(Some(path, config))
                        | Error errors ->
                            let detail = String.concat " " errors
                            Error $"Unable to load experiment: {detail}"
            with error ->
                return Error $"Unable to load experiment: {error.Message}"
        }

    let saveExperiment config =
        async {
            try
                let options = FilePickerSaveOptions(Title = "Save the FsHarness experiment")
                options.DefaultExtension <- "json"
                options.SuggestedFileName <- "experiment.json"
                options.FileTypeChoices <- [ experimentFileType () ]
                let! file = this.StorageProvider.SaveFilePickerAsync options |> Async.AwaitTask

                match file |> Option.ofObj with
                | None -> return Ok None
                | Some selected ->
                    return
                        match ConfigFile.write selected.Path.LocalPath config with
                        | Ok path -> Ok(Some path)
                        | Error errors ->
                            let detail = String.concat " " errors
                            Error $"Unable to save experiment: {detail}"
            with error ->
                return Error $"Unable to save experiment: {error.Message}"
        }

    do
        base.Title <- "FsHarness · Token-Efficient Experiment Ratchet"
        base.Width <- 1280.0
        base.Height <- 820.0
        base.MinWidth <- 1024.0
        base.MinHeight <- 680.0

        this.Closed.Add(fun _ -> (runtime :> IDisposable).Dispose())

        Program.mkProgram
            (fun () -> AppState.init runtime)
            (AppState.update runtime pickRepositoryFolder pickDataRootFolder loadExperiment saveExperiment)
            Views.view
        |> Program.withHost this
        |> Program.runWithAvaloniaSyncDispatch ()

type App() =
    inherit Application()

    override this.Initialize() =
        let theme = FluentTheme()
        theme.DensityStyle <- DensityStyle.Compact
        this.Styles.Add theme

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop -> desktop.MainWindow <- MainWindow()
        | _ -> ()

        base.OnFrameworkInitializationCompleted()

module Program =
    [<EntryPoint; STAThread>]
    let main args =
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args)
