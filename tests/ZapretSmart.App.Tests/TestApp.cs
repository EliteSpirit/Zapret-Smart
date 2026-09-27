using Avalonia;
using Avalonia.Headless;
using ZapretSmart.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace ZapretSmart.App.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
