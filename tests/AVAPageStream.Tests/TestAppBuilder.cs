using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(AVAPageStream.Tests.TestAppBuilder))]

namespace AVAPageStream.Tests;

public class TestAppBuilder
{
    // Skia rendering (not headless drawing) so frames can be captured as screenshots and pages exported.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
