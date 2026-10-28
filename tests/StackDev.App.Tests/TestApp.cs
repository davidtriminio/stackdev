using Avalonia;
using Avalonia.Headless;
using StackDev.App;

[assembly: AvaloniaTestApplication(typeof(StackDev.App.Tests.TestAppBuilder))]

namespace StackDev.App.Tests;

public static class TestAppBuilder
{
    // Skia drawing so a frame can be rendered to a picture and looked at.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<StackDev.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
