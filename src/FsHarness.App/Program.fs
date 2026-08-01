namespace FsHarness.App

open System
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Elmish
open Avalonia.FuncUI.Hosts
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

    do
        base.Title <- "FsHarness · Token-Efficient Experiment Ratchet"
        base.Width <- 1280.0
        base.Height <- 820.0
        base.MinWidth <- 1024.0
        base.MinHeight <- 680.0

        this.Closed.Add(fun _ -> (runtime :> IDisposable).Dispose())

        Program.mkProgram (fun () -> AppState.init runtime) (AppState.update runtime) Views.view
        |> Program.withHost this
        |> Program.run

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
