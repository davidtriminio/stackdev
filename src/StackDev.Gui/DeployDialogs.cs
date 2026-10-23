using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StackDev.Core.Deploy;
using StackDev.Core.Servers;

namespace StackDev.Gui;

/// <summary>Windows for the server features: verify a server, confirm an upload, provision a new API, edit its variables.</summary>
public static class DeployDialogs
{
    private static Brush P(string key) => ServerDialogs.P(key);

    private static TextBlock Muted(string text, double size = 11.5) => new()
    {
        Text = text, FontSize = size, Foreground = P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
    };

    private static TextBlock Body(string text, double size = 13) => new()
    {
        Text = text, FontSize = size, Foreground = P("TextBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
    };

    private static Button Toggle(string text)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left, MinWidth = 200, Margin = new Thickness(0, 0, 0, 12) };
        return b;
    }

    // ------------------------------------------------------------ verify a server

    /// <summary>Shows the fingerprint the server presented. True when the user confirms it is theirs.</summary>
    public static bool TrustHostKey(Window? owner, Server server, HostKeyInfo key)
    {
        var w = ServerDialogs.Base(owner, "Verificar el servidor", 620);
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(Body($"Es la primera vez que StackDev se conecta a «{server.Name}» ({server.Host}).", 14));
        root.Children.Add(Muted("Compara esta huella con la que ves en la consola de tu proveedor, o ejecuta en el servidor: ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub. Si coincide, es tu servidor y no hay nadie en medio."));
        var fp = new TextBox { Text = key.Fingerprint, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, 4, 0, 4) };
        root.Children.Add(fp);
        root.Children.Add(Muted("Tipo de clave: " + key.KeyType));
        root.Children.Add(Muted("A partir de ahora StackDev solo se conectará si la huella es esta. Si algún día cambia (por reinstalar el servidor, por ejemplo) te avisará en vez de conectarse."));
        var ok = ServerDialogs.Button("Es la correcta, confiar", true);
        var cancel = ServerDialogs.Button("Cancelar", false);
        var result = false;
        ok.Click += (_, _) => { result = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        root.Children.Add(ServerDialogs.ButtonRow(cancel, ok));
        w.Content = root;
        w.ShowDialog();
        return result;
    }

    // ------------------------------------------------------------ questions during an upload

    /// <summary>Answers the questions a deployment asks, on the window's thread (the deployment itself runs on another one).</summary>
    public sealed class GuiDecisions : IDeployDecisions
    {
        private readonly Window _owner;
        public GuiDecisions(Window owner) => _owner = owner;

        public GuardDecision BlockedFiles(IReadOnlyList<GuardFinding> blocked) =>
            _owner.Dispatcher.Invoke(() => AskBlocked(_owner, blocked));

        public bool Confirm(DeployPlan plan) => _owner.Dispatcher.Invoke(() => AskPlan(_owner, plan));

        public bool CleanAndRetry() =>
            _owner.Dispatcher.Invoke(() => Dialogs.Confirm(_owner, "La compilación falló. ¿Borro las carpetas bin y obj del proyecto y compilo de nuevo?", true));
    }

    private static GuardDecision AskBlocked(Window owner, IReadOnlyList<GuardFinding> blocked)
    {
        var w = ServerDialogs.Base(owner, "Archivos que no deben subirse", 640);
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(Body("El publish incluye archivos que no deberían estar en el servidor:", 14));
        var list = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        foreach (var f in blocked)
        {
            list.Children.Add(new TextBlock { Text = "✘ " + f.RelativePath, Foreground = P("ErrBrush"), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 });
            list.Children.Add(new TextBlock { Text = f.Reason, Foreground = P("MutedBrush"), FontSize = 11, Margin = new Thickness(16, 0, 0, 6), TextWrapping = TextWrapping.Wrap });
        }
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(Muted("Puedo quitarlos de la copia temporal que se sube (tu proyecto no se toca) y seguir, o cancelar para que lo revises."));
        var ok = ServerDialogs.Button("Quitar y continuar", true);
        var cancel = ServerDialogs.Button("Cancelar", false);
        var result = GuardDecision.Cancel;
        ok.Click += (_, _) => { result = GuardDecision.RemoveAndContinue; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        root.Children.Add(ServerDialogs.ButtonRow(cancel, ok));
        w.Content = root;
        w.ShowDialog();
        return result;
    }

    private static void Row(Panel grid, string label, string value, Brush? color = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = label, Foreground = P("MutedBrush"), FontSize = 12 });
        var v = new TextBlock { Text = value, Foreground = color ?? P("TextBrush"), FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(v, 1);
        row.Children.Add(v);
        grid.Children.Add(row);
    }

    private static bool AskPlan(Window owner, DeployPlan plan)
    {
        var w = ServerDialogs.Base(owner, "Confirmar la subida", 640);
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(Body($"Vas a subir {plan.Service} a «{plan.Server.Name}»", 15));

        var card = new Border { Style = ServerDialogs.GetStyle("Card"), Margin = new Thickness(0, 4, 0, 12) };
        var rows = new StackPanel();
        Row(rows, "Servidor", $"{plan.Server.Name}  ·  {plan.Server.Destination}  ·  {plan.Server.WebName}");
        Row(rows, "Servicio", $"{plan.Service}  ({plan.Dll})");
        Row(rows, "Dominio", string.IsNullOrEmpty(plan.Domain) ? "—" : plan.Domain!);
        Row(rows, "Contenido", $"{plan.FileCount} archivos  ·  {plan.SizeBytes / 1048576.0:0.0} MB" + (plan.RuntimeMajor is int r ? $"  ·  ASP.NET Core {r}" : ""));
        Row(rows, "Ahora", plan.ServiceActive ? "● activo (se detiene unos segundos al reemplazar)" : "○ no está activo",
            plan.ServiceActive ? P("OkBrush") : P("WarnBrush"));
        card.Child = rows;
        root.Children.Add(card);

        root.Children.Add(Muted($"Antes de reemplazar se guarda una copia de la versión actual (hay {plan.ExistingBackups} guardadas). Si la nueva no arranca bien vuelve sola a la anterior. Los datos del servidor (uploads, base de datos y el .env) no se tocan."));
        foreach (var warning in plan.Warnings)
            root.Children.Add(new TextBlock { Text = $"⚠ {warning.RelativePath}: {warning.Reason}", Foreground = P("WarnBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var gone in plan.Removed)
            root.Children.Add(new TextBlock { Text = $"✔ Quitado del paquete: {gone}", Foreground = P("OkBrush"), FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });

        root.Children.Add(new TextBlock { Text = $"Para confirmar, escribe el nombre del servicio: {plan.Service}", Foreground = P("MutedBrush"), FontSize = 11.5, Margin = new Thickness(0, 10, 0, 3) });
        var box = new TextBox();
        root.Children.Add(box);

        var ok = ServerDialogs.Button("Subir", true);
        ok.IsEnabled = false;
        var cancel = ServerDialogs.Button("Cancelar", false);
        box.TextChanged += (_, _) => ok.IsEnabled = box.Text.Trim() == plan.Service;
        var result = false;
        ok.Click += (_, _) => { result = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        root.Children.Add(ServerDialogs.ButtonRow(cancel, ok));
        w.Content = root;
        w.Loaded += (_, _) => box.Focus();
        w.ShowDialog();
        return result;
    }

    // ------------------------------------------------------------ provision

    public enum ReadinessChoice { Cancel, Install, Continue }

    /// <summary>
    /// The server check before creating an API: one line per thing looked at (✔ fine, ⚠ worth knowing, ✘ blocks).
    /// Install installs what StackDev can (web server, runtime, certbot, rsync); Continue goes on to the form.
    /// </summary>
    public static ReadinessChoice Readiness(Window? owner, Server server, IReadOnlyList<CheckItem> items)
    {
        var w = ServerDialogs.Base(owner, "Revisión del servidor", 660);
        var root = new StackPanel { Margin = new Thickness(22) };
        var errors = items.Count(c => c.Level == CheckLevel.Error);
        var warns = items.Count(c => c.Level == CheckLevel.Warn);
        root.Children.Add(Body(errors > 0 ? $"Antes de crear la API hay {errors} cosa(s) que resolver en «{server.Name}»"
            : warns > 0 ? $"«{server.Name}» está casi listo: {warns} aviso(s)"
            : $"«{server.Name}» está listo para una API nueva", 15));

        var card = new Border { Style = ServerDialogs.GetStyle("Card"), Margin = new Thickness(0, 2, 0, 12) };
        var rows = new StackPanel();
        foreach (var c in items)
        {
            var (icon, brush) = c.Level switch
            {
                CheckLevel.Ok => ("✔", P("OkBrush")),
                CheckLevel.Warn => ("⚠", P("WarnBrush")),
                _ => ("✘", P("ErrBrush")),
            };
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = icon, Foreground = brush, FontSize = 14, VerticalAlignment = VerticalAlignment.Top });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeights.SemiBold, Foreground = P("TextBrush") });
            text.Children.Add(new TextBlock { Text = c.Detail, Foreground = c.Level == CheckLevel.Ok ? P("MutedBrush") : brush, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            if (c.Level != CheckLevel.Ok && c.Manual is not null)
                text.Children.Add(new TextBlock { Text = (c.Fix != CheckFix.None ? "También a mano: " : "Cómo resolverlo: ") + c.Manual, Foreground = P("MutedBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            rows.Children.Add(row);
        }
        card.Child = rows;
        root.Children.Add(card);

        var fixes = ServerReadiness.Fixable(items);
        var canContinue = ServerReadiness.CanContinue(items);
        if (fixes.Count > 0)
            root.Children.Add(Muted("«Instalar lo que falta» usa el gestor de paquetes del servidor (dnf, yum o apt). No cambia nada más. Tarda uno o dos minutos."));
        if (!canContinue && fixes.Count == 0)
            root.Children.Add(Muted("Hay un problema que no puedo resolver desde aquí. Arréglalo en el servidor y vuelve a abrir esta revisión."));

        var cancel = ServerDialogs.Button("Cancelar", false);
        var install = ServerDialogs.Button(fixes.Count > 0 ? $"Instalar lo que falta ({fixes.Count})" : "Instalar", fixes.Count > 0 && !canContinue || fixes.Count > 0 && errors > 0);
        install.Visibility = fixes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var go = ServerDialogs.Button("Continuar", fixes.Count == 0 || canContinue);
        go.IsEnabled = canContinue;
        install.IsCancel = false;
        go.IsCancel = false;
        var choice = ReadinessChoice.Cancel;
        install.Click += (_, _) => { choice = ReadinessChoice.Install; w.Close(); };
        go.Click += (_, _) => { choice = ReadinessChoice.Continue; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        root.Children.Add(ServerDialogs.ButtonRow(cancel, install, go));
        w.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        w.SizeToContent = SizeToContent.Manual;
        w.Height = Math.Min(680, SystemParameters.WorkArea.Height - 60);
        w.ResizeMode = ResizeMode.CanResize;
        w.ShowDialog();
        return choice;
    }

    /// <summary>Asks what a new API needs: the essentials first, the rest under «Opciones avanzadas». Null when cancelled.</summary>
    public static ProvisionRequest? ProvisionForm(Window? owner, Server server, string service, string dll, string domain, int port, int? runtime)
    {
        var w = ServerDialogs.Base(owner, "Crear una API nueva", 600);
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(Muted($"Se prepara «{server.Name}» para esta API: usuario, carpetas, secretos, servicio, sitio de {server.WebName}, firewall y HTTPS. No sube código; eso lo hace «Subir API»."));

        var svc = ServerDialogs.Field(root, "Nombre del servicio", service, "Minúsculas, números y guiones, hasta 40 caracteres (ej. mi-api).", 40);
        var dom = ServerDialogs.Field(root, "Dominio público de la API", domain, "Ej. mi-api.midominio.com, sin https://. Su registro A debe apuntar a " + server.Host + " (sin proxy si usas Cloudflare).", 253);

        var kinds = new[] { DatabaseKind.Sqlite, DatabaseKind.Postgres, DatabaseKind.None };
        var names = new[] { "SQLite (archivo en el servidor)", "PostgreSQL (cadena de conexión)", "Ninguna / la configuro después" };
        var kind = 0;
        root.Children.Add(new TextBlock { Text = "Base de datos (clic para cambiar)", FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 3) });
        var dbButton = Toggle("");
        var pgPanel = new StackPanel { Visibility = Visibility.Collapsed };
        var pg = ServerDialogs.Field(pgPanel, "Cadena de conexión de PostgreSQL", "Host=localhost;Database=" + service + ";Username=postgres;Password=", null, 500);
        void SetDb()
        {
            dbButton.Content = names[kind] + "   ▾";
            pgPanel.Visibility = kinds[kind] == DatabaseKind.Postgres ? Visibility.Visible : Visibility.Collapsed;
        }
        SetDb();
        dbButton.Click += (_, _) => { kind = (kind + 1) % kinds.Length; SetDb(); };
        root.Children.Add(dbButton);
        root.Children.Add(pgPanel);

        var https = true;
        var httpsButton = Toggle("");
        var emailPanel = new StackPanel();
        var email = ServerDialogs.Field(emailPanel, "Correo para el certificado", "", "Let's Encrypt lo usa para avisarte si va a vencer.", 120);
        void SetHttps()
        {
            httpsButton.Content = (https ? "HTTPS con certificado gratis (Let's Encrypt): Sí" : "HTTPS: No, lo hago después") + "   ▾";
            emailPanel.Visibility = https ? Visibility.Visible : Visibility.Collapsed;
        }
        SetHttps();
        httpsButton.Click += (_, _) => { https = !https; SetHttps(); };
        root.Children.Add(httpsButton);
        root.Children.Add(emailPanel);

        // advanced: what is almost always fine as suggested
        var advanced = new StackPanel { Visibility = Visibility.Collapsed };
        var dllBox = ServerDialogs.Field(advanced, "Archivo .dll que arranca la API", dll, "Termina en .dll. Se detecta solo desde tu proyecto.", 100);
        var portBox = ServerDialogs.Field(advanced, "Puerto local (1024 a 65535)", port.ToString(), "Solo lo ve el proxy. Sugerido porque nada lo usa en el servidor.", 5, digitsOnly: true);
        var origin = ServerDialogs.Field(advanced, "Origen del frontend para CORS", "", "Ej. https://mi-app.vercel.app. Se puede añadir o cambiar después en «Variables».", 200);
        advanced.Children.Add(new TextBlock { Text = "Contraseña del administrador de la API", FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 3) });
        var admin = new PasswordBox { Margin = new Thickness(0, 0, 0, 2), MaxLength = 100 };
        advanced.Children.Add(admin);
        advanced.Children.Add(Muted("Vacía = se genera una fuerte y te la muestro una sola vez. La clave JWT se genera siempre."));
        var more = new Button { Content = "Opciones avanzadas  ▸", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(12, 6, 12, 6) };
        more.Click += (_, _) =>
        {
            var show = advanced.Visibility != Visibility.Visible;
            advanced.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            more.Content = "Opciones avanzadas  " + (show ? "▾" : "▸");
        };
        root.Children.Add(more);
        root.Children.Add(advanced);

        var error = ServerDialogs.Error();
        root.Children.Add(error);
        var ok = ServerDialogs.Button("Crear API", true);
        var cancel = ServerDialogs.Button("Cancelar", false);
        ProvisionRequest? result = null;
        void Problem(string message, TextBox? box, bool openAdvanced = false)
        {
            error.Text = message;
            error.Visibility = Visibility.Visible;
            if (openAdvanced && advanced.Visibility != Visibility.Visible) more.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            box?.Focus();
            box?.SelectAll();
        }
        ok.Click += (_, _) =>
        {
            if (ServerValidation.CheckDeployment(svc.Text, dllBox.Text, dom.Text) is { } bad)
            {
                if (bad.Field == "service") Problem(bad.Message, svc);
                else if (bad.Field == "dll") Problem(bad.Message, dllBox, true);
                else Problem(bad.Message, dom);
                return;
            }
            if (string.IsNullOrWhiteSpace(ServerService.CleanDomain(dom.Text))) { Problem("Escribe el dominio de la API (ej. mi-api.midominio.com).", dom); return; }
            if (dllBox.Text.Trim().Length == 0) { Problem("Escribe el archivo .dll que arranca la API (ej. MiApi.dll).", dllBox, true); return; }
            if (ServerValidation.CheckPort(portBox.Text, out var p) is { } portProblem) { Problem(portProblem.Message, portBox, true); return; }
            if (!Sh.ValidPort(p)) { Problem("El puerto debe estar entre 1024 y 65535.", portBox, true); return; }
            if (https && email.Text.Trim().Length == 0) { Problem("Escribe un correo para el certificado, o elige no pedirlo ahora.", email); return; }
            var req = new ProvisionRequest
            {
                Server = server,
                Service = svc.Text.Trim(),
                Dll = dllBox.Text.Trim(),
                Domain = ServerService.CleanDomain(dom.Text) ?? "",
                Port = p,
                Database = kinds[kind],
                PostgresConnection = pg.Text.Trim(),
                AdminPassword = admin.Password,
                FrontendOrigin = origin.Text.Trim(),
                IssueCertificate = https,
                CertificateEmail = email.Text.Trim(),
                RuntimeMajor = runtime,
            };
            if (ProvisionService.Validate(req) is { } problem) { Problem(problem, null); return; }
            result = req;
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        root.Children.Add(ServerDialogs.ButtonRow(cancel, ok));
        w.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        w.SizeToContent = SizeToContent.Manual;
        w.Height = Math.Min(700, SystemParameters.WorkArea.Height - 60);
        w.ResizeMode = ResizeMode.CanResize;
        w.Loaded += (_, _) => svc.Focus();
        w.ShowDialog();
        return result;
    }

    /// <summary>The generated admin password, shown once.</summary>
    public static void ShowSecrets(Window? owner, ProvisionSecrets secrets, string service)
    {
        var w = ServerDialogs.Base(owner, "Guarda esta contraseña", 600);
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(Body("Contraseña del administrador de la API", 14));
        var box = new TextBox { Text = secrets.AdminPassword, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, 4, 0, 8) };
        root.Children.Add(box);
        root.Children.Add(Muted($"Guárdala ahora en tu gestor de contraseñas: no se vuelve a mostrar ni queda en ningún registro. En el servidor vive solo en /etc/{service}/{service}.env, legible solo por root y el servicio."));
        var copy = ServerDialogs.Button("Copiar", false);
        copy.IsCancel = false;
        copy.Click += (_, _) => { try { Clipboard.SetText(secrets.AdminPassword); } catch (System.Runtime.InteropServices.COMException) { } };
        var done = ServerDialogs.Button("Ya la guardé", true);
        done.Click += (_, _) => w.Close();
        root.Children.Add(ServerDialogs.ButtonRow(copy, done));
        w.Content = root;
        w.ShowDialog();
    }

    // ------------------------------------------------------------ variables (.env)

    private sealed class EnvRow
    {
        public string Key = "";
        public string Original = "";
        public bool IsNew;
        public bool Removed;
        public Func<string> Value = () => "";
        public TextBox? KeyBox;
        public UIElement Element = new Grid();
    }

    /// <summary>
    /// Edits the variables. Secrets are hidden until the eye button is pressed. The file in <paramref name="env"/> is changed in place
    /// when the user saves; <paramref name="restart"/> says whether to restart the service afterwards. Returns false when nothing was saved.
    /// </summary>
    public static bool EditEnv(Window? owner, string service, EnvFile env, out bool restart)
    {
        var w = ServerDialogs.Base(owner, $"Variables de {service}", 760);
        w.SizeToContent = SizeToContent.Manual;
        w.Height = Math.Min(680, SystemParameters.WorkArea.Height - 60);
        w.ResizeMode = ResizeMode.CanResize;

        var rows = new List<EnvRow>();
        var list = new StackPanel();

        EnvRow Add(string key, string value, bool isNew)
        {
            var row = new EnvRow { Key = key, Original = value, IsNew = isNew };
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            UIElement keyElement;
            if (isNew)
            {
                row.KeyBox = new TextBox { Text = key, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, 0, 8, 0) };
                keyElement = row.KeyBox;
            }
            else
            {
                keyElement = new TextBlock { Text = key, Foreground = P("TextBrush"), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0), ToolTip = key };
            }
            grid.Children.Add(keyElement);

            var secret = !isNew && SecretMask.IsSecretName(key);
            var plain = new TextBox { Text = value, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
            var hidden = new PasswordBox { Password = value };
            Grid.SetColumn(plain, 1);
            Grid.SetColumn(hidden, 1);
            plain.Visibility = secret ? Visibility.Collapsed : Visibility.Visible;
            hidden.Visibility = secret ? Visibility.Visible : Visibility.Collapsed;
            grid.Children.Add(plain);
            grid.Children.Add(hidden);
            row.Value = () => hidden.Visibility == Visibility.Visible ? hidden.Password : plain.Text;

            if (secret)
            {
                var eye = new Button { Content = "👁", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 6, 10, 6), ToolTip = "Mostrar u ocultar" };
                eye.Click += (_, _) =>
                {
                    if (hidden.Visibility == Visibility.Visible) { plain.Text = hidden.Password; plain.Visibility = Visibility.Visible; hidden.Visibility = Visibility.Collapsed; }
                    else { hidden.Password = plain.Text; hidden.Visibility = Visibility.Visible; plain.Visibility = Visibility.Collapsed; }
                };
                Grid.SetColumn(eye, 2);
                grid.Children.Add(eye);
            }
            var remove = new Button { Content = "✕", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 6, 10, 6), ToolTip = "Quitar esta variable" };
            remove.Click += (_, _) => { row.Removed = true; grid.Visibility = Visibility.Collapsed; };
            Grid.SetColumn(remove, 3);
            grid.Children.Add(remove);

            row.Element = grid;
            rows.Add(row);
            list.Children.Add(grid);
            return row;
        }

        foreach (var v in env.Variables) Add(v.Key!, v.Value, isNew: false);

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new StackPanel();
        head.Children.Add(Muted("Las claves, contraseñas y cadenas de conexión están ocultas. Antes de guardar se hace una copia del archivo actual en el servidor; si el servicio no arranca con los valores nuevos, vuelve el archivo anterior."));
        root.Children.Add(head);
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 4) };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var foot = new StackPanel();
        var tools = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var addVar = new Button { Content = "Agregar variable", Style = ServerDialogs.GetStyle("BtnChip") };
        var addCors = new Button { Content = "Agregar origen CORS", Style = ServerDialogs.GetStyle("BtnChip") };
        tools.Children.Add(addVar);
        tools.Children.Add(addCors);
        foot.Children.Add(tools);
        var error = ServerDialogs.Error();
        error.Margin = new Thickness(0, 8, 0, 0);
        foot.Children.Add(error);
        var save = ServerDialogs.Button("Guardar y reiniciar", true);
        var saveOnly = ServerDialogs.Button("Guardar sin reiniciar", false);
        saveOnly.IsCancel = false;
        var cancel = ServerDialogs.Button("Cancelar", false);
        foot.Children.Add(ServerDialogs.ButtonRow(cancel, saveOnly, save));
        Grid.SetRow(foot, 2);
        root.Children.Add(foot);

        addVar.Click += (_, _) => { var r = Add("", "", isNew: true); scroll.ScrollToEnd(); r.KeyBox?.Focus(); };
        addCors.Click += (_, _) =>
        {
            var next = rows.Where(r => !r.Removed).Select(r => r.KeyBox?.Text.Trim() ?? r.Key)
                .Select(k => Regex.Match(k, @"^Cors__AllowedOrigins__(\d+)$")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max() + 1;
            Add($"Cors__AllowedOrigins__{next}", "https://", isNew: true);
            scroll.ScrollToEnd();
        };

        var saved = false;
        var doRestart = false;
        void Apply(bool withRestart)
        {
            var wanted = new Dictionary<string, string>();
            foreach (var r in rows.Where(r => !r.Removed))
            {
                var key = (r.IsNew ? r.KeyBox!.Text : r.Key).Trim();
                if (key.Length == 0) continue;
                if (!EnvFile.IsValidKey(key)) { error.Text = $"«{key}» no es un nombre válido: letras, números y guiones bajos, sin empezar con número."; error.Visibility = Visibility.Visible; return; }
                if (wanted.ContainsKey(key)) { error.Text = $"«{key}» está repetida."; error.Visibility = Visibility.Visible; return; }
                var value = r.Value();
                if (value.Contains('\n') || value.Contains('\r')) { error.Text = $"El valor de «{key}» no puede tener saltos de línea."; error.Visibility = Visibility.Visible; return; }
                wanted[key] = value;
            }
            var changed = false;
            foreach (var r in rows.Where(r => !r.IsNew && (r.Removed || !wanted.ContainsKey(r.Key))))
                changed |= env.Remove(r.Key);
            foreach (var (k, v) in wanted)
            {
                if (env.Get(k) == v) continue;
                env.Set(k, v);
                changed = true;
            }
            if (!changed) { error.Text = "No hay cambios que guardar."; error.Visibility = Visibility.Visible; return; }
            saved = true;
            doRestart = withRestart;
            w.Close();
        }
        save.Click += (_, _) => Apply(true);
        saveOnly.Click += (_, _) => Apply(false);
        cancel.Click += (_, _) => w.Close();
        w.Content = root;
        w.ShowDialog();
        restart = doRestart;
        return saved;
    }
}
