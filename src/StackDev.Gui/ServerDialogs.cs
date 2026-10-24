using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StackDev.Core.Config;
using StackDev.Core.Deploy;
using StackDev.Core.Servers;

namespace StackDev.Gui;

/// <summary>Ventanas para agregar o editar un servidor y para decir a qué servidor y servicio se sube un proyecto.</summary>
public static class ServerDialogs
{
    internal static Brush P(string key) => (Brush)Application.Current.FindResource(key);
    internal static Style GetStyle(string key) => (Style)Application.Current.FindResource(key);

    internal static Window Base(Window? owner, string title, double width)
    {
        var w = new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        TitleBar.Apply(w);
        return w;
    }

    internal static TextBox Field(Panel panel, string label, string val, string? help = null, int maxLength = 0, bool digitsOnly = false)
    {
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 3) });
        var box = new TextBox { Text = val, Margin = new Thickness(0, 0, 0, help is null ? 12 : 2) };
        if (maxLength > 0) box.MaxLength = maxLength;
        if (digitsOnly)
        {
            box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
            DataObject.AddPastingHandler(box, (_, e) =>
            {
                if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted || !pasted.Trim().All(char.IsAsciiDigit)) e.CancelCommand();
            });
        }
        panel.Children.Add(box);
        if (help is not null)
            panel.Children.Add(new TextBlock { Text = help, FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap });
        return box;
    }

    internal static Button Button(string text, bool primary)
    {
        var b = new Button { Content = text, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        if (primary) { b.Style = GetStyle("BtnPrimary"); b.IsDefault = true; } else b.IsCancel = true;
        return b;
    }

    internal static StackPanel ButtonRow(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    internal static TextBlock Error() => new()
    {
        Foreground = P("ErrBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), Visibility = Visibility.Collapsed,
    };

    /// <summary>Agrega (actual = null) o edita un servidor. Devuelve null si se cancela.</summary>
    public static Server? EditServer(Window? owner, Server? current)
    {
        var s = current is null
            ? new Server()
            : new Server { Id = current.Id, Name = current.Name, Host = current.Host, User = current.User, Port = current.Port, Key = current.Key, Web = current.Web };
        var w = Base(owner, current is null ? "Agregar servidor" : "Editar servidor", 560);
        var root = new StackPanel { Margin = new Thickness(22) };

        var name = Field(root, "Nombre (para reconocerlo)", s.Name, "Por ejemplo: Oracle producción, VPS clientes.", ServerValidation.NameMax);
        var host = Field(root, "IP o dominio del servidor", s.Host, "Solo la IP o el dominio, sin usuario ni puerto. La ves en la consola de tu proveedor (Oracle, AWS…) > Instancias > IP pública.", ServerValidation.HostMax);
        var user = Field(root, "Usuario SSH", s.User, "Típicos: opc (Oracle Linux), ubuntu (Ubuntu), ec2-user (Amazon Linux), root.", ServerValidation.UserMax);
        var port = Field(root, "Puerto SSH (1 a 65535)", s.Port.ToString(), "Casi siempre es 22.", 5, digitsOnly: true);

        root.Children.Add(new TextBlock { Text = "Clave SSH (el archivo privado, no el .pub)", FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 3) });
        var rowKey = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        rowKey.ColumnDefinitions.Add(new ColumnDefinition());
        rowKey.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var key = new TextBox { Text = s.Key, MaxLength = 260 };
        var browse = new Button { Content = "Examinar…", Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(browse, 1);
        rowKey.Children.Add(key);
        rowKey.Children.Add(browse);
        root.Children.Add(rowKey);
        browse.Click += (_, _) =>
        {
            var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Elige la clave SSH privada", InitialDirectory = Directory.Exists(home) ? home : null };
            if (dlg.ShowDialog(w) == true) key.Text = dlg.FileName;
        };

        root.Children.Add(new TextBlock { Text = "Servidor web (proxy hacia tus APIs)", FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 3) });
        var nginx = string.Equals(s.Web, "nginx", StringComparison.OrdinalIgnoreCase);
        var web = new Button { HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left, MinWidth = 160, Margin = new Thickness(0, 0, 0, 6) };
        void SetWeb() => web.Content = (nginx ? "Nginx" : "Apache") + "   ▾";
        SetWeb();
        web.Click += (_, _) => { nginx = !nginx; SetWeb(); };
        root.Children.Add(web);
        root.Children.Add(new TextBlock { Text = "Clic para alternar entre Apache y Nginx.", FontSize = 11, Foreground = P("MutedBrush") });

        var error = Error();
        root.Children.Add(error);
        var ok = Button("Guardar", true);
        var cancel = Button("Cancelar", false);
        Server? result = null;
        var boxes = new Dictionary<string, TextBox> { ["name"] = name, ["host"] = host, ["user"] = user, ["port"] = port, ["key"] = key };
        ok.Click += (_, _) =>
        {
            host.Text = ServerValidation.CleanHost(host.Text);
            if (ServerValidation.CheckServer(name.Text, host.Text, user.Text, port.Text, key.Text.Trim().Trim('"'), out var portNumber) is { } problem)
            {
                error.Text = problem.Message;
                error.Visibility = Visibility.Visible;
                if (boxes.TryGetValue(problem.Field, out var bad)) { bad.Focus(); bad.SelectAll(); }
                return;
            }
            s.Name = name.Text.Trim();
            s.Host = host.Text.Trim();
            s.User = user.Text.Trim();
            s.Port = portNumber;
            s.Key = key.Text.Trim().Trim('"');
            s.Web = nginx ? "nginx" : "apache";
            result = s;
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        root.Children.Add(ButtonRow(cancel, ok));
        w.Content = root;
        w.Loaded += (_, _) => name.Focus();
        w.ShowDialog();
        return result;
    }

    /// <summary>Elige a qué servidor y servicio se sube la API de un proyecto. Devuelve null si se cancela.</summary>
    public static DeploymentConfig? EditDeployment(Window? owner, IReadOnlyList<Server> servers, DeploymentConfig? current, string suggestionService, string suggestionDll)
    {
        if (servers.Count == 0)
        {
            Dialogs.Confirm(owner, "Primero agrega un servidor (botón «Agregar servidor»).", true);
            return null;
        }
        var w = Base(owner, "Despliegue de este proyecto", 560);
        var root = new StackPanel { Margin = new Thickness(22) };

        var index = Math.Max(0, servers.ToList().FindIndex(x => x.Id == current?.Server));
        root.Children.Add(new TextBlock { Text = "Servidor", FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 3) });
        var buttonServer = new Button { HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left, MinWidth = 260, Margin = new Thickness(0, 0, 0, 12) };
        void SetServer() => buttonServer.Content = servers[index].Name + "   ▾";
        SetServer();
        buttonServer.Click += (_, _) =>
        {
            var i = Dialogs.Choose(w, "¿A qué servidor se sube?", servers.Select(x => $"{x.Name}   ·   {x.Destination}").ToList(), index);
            if (i >= 0) { index = i; SetServer(); }
        };
        root.Children.Add(buttonServer);

        var service = Field(root, "Nombre del servicio en el servidor", current?.Service ?? suggestionService,
            "Minúsculas, números y guiones, hasta 40 caracteres (ej. mi-api). Es el servicio systemd y la carpeta en /var/www. Si ya está instalado, usa el botón de abajo para elegirlo.", 40);
        TextBox? dllBox = null;
        var find = new Button { Content = "Buscar APIs ya instaladas en este servidor…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        find.Click += async (_, _) =>
        {
            find.IsEnabled = false;
            find.Content = "Consultando el servidor…";
            string? err = null;
            var server = servers[index];
            var apis = await System.Threading.Tasks.Task.Run(() => DeployService.DiscoverApis(server, out err));
            find.IsEnabled = true;
            find.Content = "Buscar APIs ya instaladas en este servidor…";
            if (err is not null) { Dialogs.Confirm(w, err, true); return; }
            if (apis.Count == 0) { Dialogs.Confirm(w, "No encontré servicios .NET instalados en ese servidor. Si es una API nueva, usa «Aprovisionar API nueva».", true); return; }
            var i = Dialogs.Choose(w, "APIs .NET instaladas en el servidor", apis.Select(a => $"{a.Service}   ·   {a.Dll}   ·   {a.State}").ToList(), 0);
            if (i < 0) return;
            service.Text = apis[i].Service;
            if (apis[i].Dll.Length > 0 && dllBox is not null) dllBox.Text = apis[i].Dll;
        };
        root.Children.Add(find);
        dllBox = Field(root, "Archivo .dll que arranca la API", current?.Dll ?? suggestionDll, "Termina en .dll (ej. MiApi.dll). Se detecta solo al buscar tu proyecto.", 100);
        var domain = Field(root, "Dominio público de la API (opcional)", current?.Domain ?? "",
            "Sin https://. Sirve para comprobar que responde y cuándo vence el certificado.", 253);

        var error = Error();
        root.Children.Add(error);
        var ok = Button("Guardar", true);
        var cancel = Button("Cancelar", false);
        DeploymentConfig? result = null;
        ok.Click += (_, _) =>
        {
            var svc = service.Text.Trim();
            if (ServerValidation.CheckDeployment(svc, dllBox.Text, domain.Text) is { } problem)
            {
                error.Text = problem.Message;
                error.Visibility = Visibility.Visible;
                var bad = problem.Field switch { "service" => service, "dll" => dllBox!, _ => domain };
                bad.Focus(); bad.SelectAll();
                return;
            }
            result = new DeploymentConfig
            {
                Server = servers[index].Id,
                Service = svc,
                Dll = dllBox.Text.Trim().Length == 0 ? null : dllBox.Text.Trim(),
                Project = current?.Project,
                Domain = ServerService.CleanDomain(domain.Text),
                Extra = current?.Extra,
            };
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        root.Children.Add(ButtonRow(cancel, ok));
        w.Content = root;
        w.ShowDialog();
        return result;
    }
}
