namespace FsHarness.App

open System
open System.IO
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Elmish
open Avalonia.FuncUI.Hosts
open Avalonia.Platform.Storage
open Avalonia.Themes.Fluent
open Avalonia.Threading
open Elmish
open FsHarness.Codex
open FsHarness.Infrastructure

[<RequireQualifiedAccess>]
module WindowTitle =
    let private compactPath maxLength (path: string) =
        if path.Length <= maxLength then
            path
        else
            let visibleLength = maxLength - 1
            let leftLength = visibleLength / 2
            let rightLength = visibleLength - leftLength
            $"{path.Substring(0, leftLength)}…{path.Substring(path.Length - rightLength)}"

    let forExperiment (experimentFile: string option) =
        match experimentFile with
        | None -> "FsHarness · Token-Efficient Experiment Ratchet"
        | Some path ->
            let fileName = Path.GetFileName path
            let directory = Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue ""

            if String.IsNullOrEmpty directory then
                $"FsHarness · {fileName}"
            else
                $"FsHarness · {fileName} — {compactPath 54 directory}"

type MainWindow() as this =
    inherit HostWindow()

    let configuredCodexPath =
        match Environment.GetEnvironmentVariable "FSHARNESS_CODEX_PATH" with
        | value when not (String.IsNullOrWhiteSpace value) -> Some value
        | _ -> None

    let codexExecutable = (CliDiscovery.resolve configuredCodexPath).Executable

    let runtime = new HarnessRuntime(codexExecutable)

    let runPickerOnUiThread (picker: unit -> Task<'result>) : Async<'result> =
        let completion = TaskCompletionSource<'result>()

        Dispatcher.UIThread.Post(
            Action(fun () ->
                Async.StartImmediate(
                    async {
                        try
                            let! result = picker () |> Async.AwaitTask
                            completion.TrySetResult result |> ignore
                        with error ->
                            completion.TrySetException error |> ignore
                    }
                ))
        )

        completion.Task |> Async.AwaitTask

    let pickRepositoryFolder () =
        async {
            try
                let options =
                    FolderPickerOpenOptions(Title = "Select a source repository", AllowMultiple = false)

                let! folders = runPickerOnUiThread (fun () -> this.StorageProvider.OpenFolderPickerAsync options)

                return folders |> Seq.tryHead |> Option.map (fun folder -> folder.Path.LocalPath) |> Ok
            with error ->
                return Error $"Unable to open the repository folder picker: {error.Message}"
        }

    let pickDataRootFolder () =
        async {
            try
                let options =
                    FolderPickerOpenOptions(Title = "Select the FsHarness data root", AllowMultiple = false)

                let! folders = runPickerOnUiThread (fun () -> this.StorageProvider.OpenFolderPickerAsync options)

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
                let! files = runPickerOnUiThread (fun () -> this.StorageProvider.OpenFilePickerAsync options)

                match files |> Seq.tryHead with
                | None -> return Ok None
                | Some file ->
                    let path = file.Path.LocalPath

                    return
                        match ConfigFile.read path with
                        | Ok config ->
                            match ConfigFile.readDataRoot path with
                            | Ok dataRoot -> Ok(Some(path, config, dataRoot))
                            | Error errors ->
                                let detail = String.concat " " errors
                                Error $"Unable to load experiment data root: {detail}"
                        | Error errors ->
                            let detail = String.concat " " errors
                            Error $"Unable to load experiment: {detail}"
            with error ->
                return Error $"Unable to load experiment: {error.Message}"
        }

    let saveExperiment dataRoot config =
        async {
            try
                let options = FilePickerSaveOptions(Title = "Save the FsHarness experiment")
                options.DefaultExtension <- "json"
                options.SuggestedFileName <- "experiment.json"
                options.FileTypeChoices <- [ experimentFileType () ]
                let! file = runPickerOnUiThread (fun () -> this.StorageProvider.SaveFilePickerAsync options)

                match file |> Option.ofObj with
                | None -> return Ok None
                | Some selected ->
                    return
                        match ConfigFile.writeWithDataRoot (Some dataRoot) selected.Path.LocalPath config with
                        | Ok path -> Ok(Some path)
                        | Error errors ->
                            let detail = String.concat " " errors
                            Error $"Unable to save experiment: {detail}"
            with error ->
                return Error $"Unable to save experiment: {error.Message}"
        }

    do
        base.Title <- WindowTitle.forExperiment None
        base.Width <- 1280.0
        base.Height <- 820.0
        base.MinWidth <- 1024.0
        base.MinHeight <- 680.0

        this.Closed.Add(fun _ -> (runtime :> IDisposable).Dispose())

        let view model dispatch =
            this.Title <- WindowTitle.forExperiment model.ExperimentFile
            this.FontSize <- 13.0 * model.FontScale
            Views.view model dispatch

        Program.mkProgram
            (fun () -> AppState.init runtime)
            (AppState.update
                runtime
                pickRepositoryFolder
                pickDataRootFolder
                loadExperiment
                saveExperiment
                codexExecutable)
            view
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
