namespace FsHarness.Tests

open System
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Input
open Avalonia.VisualTree
open FsHarness.App
open FsHarness.Core
open FsHarness.Infrastructure
open Xunit

type HeadlessAppBuilder =
    static member BuildAvaloniaApp() =
        let options = AvaloniaHeadlessPlatformOptions()
        options.UseHeadlessDrawing <- false
        AppBuilder.Configure<App>().UseSkia().UseHeadless(options)

module UiTests =
    [<Fact>]
    let ``shell renders at both supported viewport sizes and accepts keyboard focus`` () =
        use session = HeadlessUnitTestSession.StartNew(typeof<HeadlessAppBuilder>)

        session
            .Dispatch(
                Action(fun () ->
                    for width, height in [ 1024.0, 680.0; 1440.0, 900.0 ] do
                        let window = new MainWindow()
                        window.Width <- width
                        window.Height <- height
                        window.Show()

                        use frame = window.CaptureRenderedFrame()
                        Assert.NotNull frame
                        Assert.True(frame.PixelSize.Width >= int width)
                        Assert.True(frame.PixelSize.Height >= int height)

                        let browse =
                            window.GetVisualDescendants()
                            |> Seq.choose (function
                                | :? Button as button when string button.Content = "Browse…" -> Some button
                                | _ -> None)
                            |> Seq.tryExactlyOne

                        Assert.True(browse.IsSome, "The repository field should expose a folder picker.")

                        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None)
                        Assert.NotNull(window.FocusManager.GetFocusedElement())
                        window.Close()),
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult()

    [<Fact>]
    let ``folder selection invalidates repository inspection and setup remains renderable`` () =
        use runtime = new HarnessRuntime("codex")
        let model, _ = AppState.init runtime

        let inspected =
            { TopLevel = "C:\\old"
              CommonDirectory = "C:\\old\\.git"
              Head = CommitOid.create (String('a', 40))
              Branch = Some "main"
              IsDirty = false
              DirtySummary = ""
              HasSubmodules = false
              IsSparseCheckout = false }

        let selectedPath = "C:\\selected"

        let selected, _ =
            AppState.update
                runtime
                (fun () -> async { return Ok None })
                (RepositoryFolderSelected(Ok(Some selectedPath)))
                { model with
                    Repository = Some inspected }

        Assert.Equal(selectedPath, selected.Draft.SourcePath)
        Assert.True(selected.Repository.IsNone)
        Assert.NotNull(Views.view selected ignore)
