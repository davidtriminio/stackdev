using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using StackDev.Core.State;
using Xunit;

namespace StackDev.App.Tests;

public class MainWindowTests
{
    private static string Fixture(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "api"));
        Directory.CreateDirectory(Path.Combine(root, "web"));
        File.WriteAllText(Path.Combine(root, "dev-menu.json"), """
            {
              "name": "demo",
              "api": { "folder": "api", "url": "http://localhost:18765", "start": "echo api", "test": "", "build": "" },
              "web": { "folder": "web", "url": "http://localhost:18766", "start": "echo web", "test": "", "build": "", "install": "" }
            }
            """);
        return root;
    }

    private static async Task<T> WaitFor<T>(Func<T?> probe, int seconds = 30) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            if (probe() is { } found) return found;
            await Task.Delay(100);
        }
        throw new TimeoutException("The window did not reach the expected state.");
    }

    [AvaloniaFact]
    public async Task Opening_a_project_shows_a_card_for_its_api_and_web_with_their_state()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "stackdev-app-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var project = Fixture(Path.Combine(tmp, "demo"));
            var window = new MainWindow(project, new StateStore(Path.Combine(tmp, "state")));
            window.Show();

            var cards = await WaitFor(() => window.FindControl<WrapPanel>("Cards") is { Children.Count: 2 } c ? c : null);
            Assert.Equal(new[] { "API", "Web" }, cards.Children.OfType<ServiceCard>().Select(c => c.Title));
            Assert.Equal("demo", window.FindControl<TextBlock>("Heading")!.Text);

            // The state is read from the real ports: nothing listens on these ones.
            await WaitFor(() => cards.Children.OfType<ServiceCard>().All(c => c.StateText == "Detenida") ? cards : null);
            Assert.Equal(new[] { "http://localhost:18765", "http://localhost:18766" }, cards.Children.OfType<ServiceCard>().Select(c => c.UrlText));

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(Path.GetTempPath(), "stackdev-app-window.png"));
            window.Close();
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }

    [AvaloniaFact]
    public void Without_a_project_the_window_asks_to_open_one()
    {
        var window = new MainWindow(null, new StateStore(Path.Combine(Path.GetTempPath(), "stackdev-app-empty-" + Guid.NewGuid().ToString("N")[..8])));
        window.Show();
        Assert.Contains("Abre la carpeta", window.FindControl<TextBlock>("Subtitle")!.Text);
        Assert.Empty(window.FindControl<WrapPanel>("Cards")!.Children);
        window.Close();
    }
}
