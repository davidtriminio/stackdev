using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StackDev.Core.Credentials;

namespace StackDev.Gui;

/// <summary>
/// La tarjeta «Credenciales» del panel: usuarios y contraseñas de prueba del proyecto, con ojo para mostrar u ocultar
/// (todas a la vez o cada una), botones para copiar y para agregar, editar y quitar.
/// </summary>
public sealed class CredentialsCard : Border
{
    private const string Font = "Segoe MDL2 Assets";
    private const string IcEye = "", IcEyeOff = "", IcCopy = "", IcReady = "",
                         IcEdit = "", IcRemove = "", IcMore = "";

    private readonly CredentialsStore _store;
    private readonly Window? _owner;
    private readonly StackPanel _list = new();
    private readonly Button _globalEye;
    private readonly HashSet<int> _visible = new();
    private bool _showAll;

    public CredentialsCard(CredentialsStore store, Window? owner)
    {
        _store = store;
        _owner = owner;
        Style = (Style)Application.Current.FindResource("Card");
        Width = 380;
        Margin = new Thickness(0, 0, 16, 16);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Icons.Badge("CREDENCIALES"));
        title.Children.Add(new TextBlock { Text = "Credenciales", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(title);
        _globalEye = Icon(IcEye, "Mostrar u ocultar todas las contraseñas", ToggleAll);
        var more = Icon(IcMore, "Agregar credencial", () => Edit(-1));
        Grid.SetColumn(_globalEye, 1);
        Grid.SetColumn(more, 2);
        header.Children.Add(_globalEye);
        header.Children.Add(more);

        var root = new StackPanel();
        root.Children.Add(header);
        _list.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_list);
        Child = root;
        Refresh();
    }

    private static Brush P(string key) => (Brush)Application.Current.FindResource(key);

    private Button Icon(string glyph, string help, Action toDoClick)
    {
        var b = new Button
        {
            Content = glyph,
            FontFamily = new FontFamily(Font),
            FontSize = 13,
            Style = (Style)Application.Current.FindResource("BtnIcon"),
            ToolTip = help,
            Padding = new Thickness(2),
        };
        b.Click += (_, _) => toDoClick();
        return b;
    }

    private void ToggleAll()
    {
        _showAll = !_showAll;
        _visible.Clear();
        Refresh();
    }

    private async void Copy(Button button, string text)
    {
        try { Clipboard.SetText(text); }
        catch (ExternalException) { return; }   // el portapapeles lo tiene otro programa en este instante
        button.Content = IcReady;
        await Task.Delay(1000);
        button.Content = IcCopy;
    }

    private void Refresh()
    {
        _globalEye.Content = _showAll ? IcEyeOff : IcEye;
        _list.Children.Clear();
        var data = _store.Read();
        if (data.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "Aún no hay ninguna. Con + guardas usuarios y contraseñas de prueba para no olvidarlos.",
                Foreground = P("MutedBrush"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        for (var i = 0; i < data.Count; i++) _list.Children.Add(Row(i, data[i]));
    }

    private UIElement Row(int index, Credential c)
    {
        var content = new StackPanel();

        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock { Text = c.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var edit = Icon(IcEdit, "Editar", () => Edit(index));
        var remove = Icon(IcRemove, "Eliminar", () => Remove(index));
        Grid.SetColumn(edit, 1);
        Grid.SetColumn(remove, 2);
        title.Children.Add(edit);
        title.Children.Add(remove);
        content.Children.Add(title);

        if (c.User.Length > 0) content.Children.Add(Row("Usuario", c.User, c.User, null));

        if (c.Key.Length > 0)
        {
            var visible = _showAll || _visible.Contains(index);
            var eye = Icon(visible ? IcEyeOff : IcEye, visible ? "Ocultar" : "Mostrar", () =>
            {
                if (!_visible.Remove(index)) _visible.Add(index);
                Refresh();
            });
            content.Children.Add(Row("Contraseña", visible ? c.Key : "••••••••", c.Key, eye));
        }

        if (c.Note.Length > 0)
            content.Children.Add(new TextBlock { Text = c.Note, Foreground = P("MutedBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

        return new Border
        {
            Background = P("Surface2Brush"),
            BorderBrush = P("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 6, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = content,
        };
    }

    /// <summary>«Etiqueta  valor  [ojo] [copiar]».</summary>
    private Grid Row(string label, string shown, string aCopy, Button? eye)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = label, Foreground = P("MutedBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var val = new TextBlock
        {
            Text = shown,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = eye is null ? aCopy : null,
        };
        Grid.SetColumn(val, 1);
        g.Children.Add(val);
        if (eye is not null) { Grid.SetColumn(eye, 2); g.Children.Add(eye); }
        Button? copy = null;
        copy = Icon(IcCopy, "Copiar", () => Copy(copy!, aCopy));
        Grid.SetColumn(copy, 3);
        g.Children.Add(copy);
        return g;
    }

    private void Remove(int index)
    {
        var data = _store.Read();
        if (index < 0 || index >= data.Count) return;
        if (!Dialogs.Confirm(_owner, $"¿Eliminar «{data[index].Name}»?", false)) return;
        data.RemoveAt(index);
        _store.Save(data);
        _visible.Clear();
        Refresh();
    }

    private void Edit(int index)
    {
        var data = _store.Read();
        var current = index >= 0 && index < data.Count ? data[index] : new Credential();

        var w = new Window
        {
            Title = index < 0 ? "Nueva credencial" : "Editar credencial",
            Owner = _owner,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = _owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        TitleBar.Apply(w);

        var root = new StackPanel { Margin = new Thickness(22) };
        TextBox Field(string label, string val, bool several = false)
        {
            root.Children.Add(new TextBlock { Text = label, Foreground = P("MutedBrush"), FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
            var t = new TextBox { Text = val };
            if (several) { t.AcceptsReturn = true; t.TextWrapping = TextWrapping.Wrap; t.MinHeight = 56; t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
            root.Children.Add(t);
            return t;
        }
        var name = Field("Nombre (p. ej. «Admin web», «Usuario de prueba»)", current.Name);
        var user = Field("Usuario o correo", current.User);
        var key = Field("Contraseña", current.Key);
        var note = Field("Nota (opcional)", current.Note, several: true);

        var ok = new Button { Content = "Guardar", MinWidth = 90, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.FindResource("BtnPrimary"), IsDefault = true };
        var cancel = new Button { Content = "Cancelar", MinWidth = 90, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        w.Content = root;

        var saved = false;
        ok.Click += (_, _) => { saved = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        w.ShowDialog();
        if (!saved) return;

        var fresh = new Credential
        {
            Name = name.Text.Trim(),
            User = user.Text.Trim(),
            Key = key.Text,
            Note = note.Text.Trim(),
        };
        if (fresh.Name.Length == 0) fresh.Name = fresh.User.Length > 0 ? fresh.User : "Credencial";
        if (index >= 0 && index < data.Count) data[index] = fresh; else data.Add(fresh);
        _store.Save(data);
        Refresh();
    }
}
