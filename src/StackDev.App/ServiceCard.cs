using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using StackDev.Core.Config;
using StackDev.Core.Services;

namespace StackDev.App;

/// <summary>A card for the API or the web app: its state, its address and the buttons to start, stop, restart, open and follow its log.</summary>
public sealed class ServiceCard : Border
{
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _state = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _url = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };

    public ProjectComponent Component { get; }
    public string Title { get; }
    public string StateText => _state.Text ?? "";
    public string UrlText => _url.Text ?? "";

    /// <param name="onAction">Called with the component and one of "start", "stop", "restart", "open", "log".</param>
    public ServiceCard(ProjectComponent component, string title, Action<ProjectComponent, string> onAction)
    {
        Component = component;
        Title = title;
        Classes.Add("card");

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(_dot);
        head.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(_state);

        _url.Classes.Add("muted");
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        Button Make(string text, string action, bool primary = false)
        {
            var b = new Button { Content = text };
            b.Classes.Add("sm");
            if (primary) b.Classes.Add("primary");
            b.Click += (_, _) => onAction(component, action);
            return b;
        }
        buttons.Children.Add(Make("Iniciar", "start", primary: true));
        buttons.Children.Add(Make("Detener", "stop"));
        buttons.Children.Add(Make("Reiniciar", "restart"));
        buttons.Children.Add(Make("Abrir", "open"));
        buttons.Children.Add(Make("Ver log", "log"));

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(_url);
        body.Children.Add(buttons);
        Child = body;
        Update(new ServiceState(ServiceCode.Stopped, "consultando…"));
    }

    public void Update(ServiceState st, string? configuredUrl = null)
    {
        _state.Text = st.Text switch { "CORRIENDO" => "Corriendo", "ARRANCANDO" => "Arrancando", "DETENIDA" => "Detenida", var t => t };
        _url.Text = st.Url ?? configuredUrl ?? "";
        _dot.Fill = st.Code switch
        {
            ServiceCode.Running => Brush.Parse("#22C55E"),
            ServiceCode.Starting => Brush.Parse("#F59E0B"),
            ServiceCode.Error => Brush.Parse("#EF4444"),
            _ => Brush.Parse("#8A8F98"),
        };
    }
}
