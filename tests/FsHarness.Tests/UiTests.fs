namespace FsHarness.Tests

open System
open System.Threading
open Avalonia
open Avalonia.Headless
open Avalonia.Input
open FsHarness.App
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

                        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None)
                        Assert.NotNull(window.FocusManager.GetFocusedElement())
                        window.Close()),
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult()
