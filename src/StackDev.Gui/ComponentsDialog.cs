using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using StackDev.Core;
using StackDev.Core.Config;
using StackDev.Core.Detection;
using StackDev.Core.Util;

namespace StackDev.Gui;

/// <summary>
/// Para corregir qué tiene el proyecto (Postgres, API, web, móvil) y sus comandos principales sin tocar el JSON a mano.
/// Trabaja sobre una copia: solo se guarda si se pulsa «Guardar».
/// </summary>
public static class ComponentsDialog
{
    private static Brush P(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>
    /// Entiende NOMBRE=valor, $env:NOMBRE = "valor" y set NOMBRE=valor. Lo que no parezca una variable (un comando, por ejemplo) se ignora.
    /// </summary>
    public static Dictionary<string, string> ParseEnv(string text)
    {
        var res = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim().TrimEnd(';');
            if (l.Length == 0 || l.StartsWith('#')) continue;
            if (l.StartsWith("$env:", StringComparison.OrdinalIgnoreCase)) l = l[5..];
            else if (l.StartsWith("set ", StringComparison.OrdinalIgnoreCase)) l = l[4..].TrimStart();
            var i = l.IndexOf('=');
            if (i <= 0) continue;
            var name = l[..i].Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$")) continue;
            var val = l[(i + 1)..].Trim();
            if (val.Length >= 2 && (val[0] == '"' || val[0] == '\'') && val[^1] == val[0]) val = val[1..^1];
            res[name] = val;
        }
        return res;
    }

    private static string? Null(string t) => t.Length == 0 ? null : t;

    public static ProjectConfig? Show(Window owner, Session s)
    {
        var cfg = ConfigJson.Read(ConfigJson.Write(s.Ctx.Config));   // copia de trabajo
        var detected = Detector.Detect(s.Root);
        var slug = Paths.Slug(Paths.FolderName(s.Root));

        var alreadyPg = cfg.Postgres is not null;
        var alreadyApi = cfg.Api is not null;
        var alreadyWeb = cfg.Web is not null;
        var alreadyMobile = cfg.Mobile is not null;

        // Si está desactivado se muestran valores sugeridos (lo detectado o unos por defecto) por si lo activan.
        var pg = cfg.Postgres ?? new PostgresConfig
        {
            Container = slug + "-db", Image = "postgres:17-alpine", Port = 5432,
            User = "postgres", Password = "postgres", Database = slug, Volume = slug + "-pgdata",
        };
        var api = cfg.Api ?? detected.Config.Api ?? new ApiConfig
        {
            Folder = ".", Url = "http://localhost:5000", Start = "dotnet run", Test = "dotnet test", Build = "dotnet build",
        };
        var web = cfg.Web ?? detected.Config.Web ?? new WebConfig
        {
            Folder = ".", Url = "http://localhost:4200", Start = "pnpm start", Test = "pnpm test", Build = "pnpm build", Install = "pnpm install",
        };
        var mobile = cfg.Mobile ?? detected.Config.Mobile ?? new MobileConfig
        {
            Folder = ".", Parse = "flutter analyze", Test = "flutter test", Format = "dart format .",
            Generate = "dart run build_runner build --delete-conflicting-outputs",
        };

        var apply = new List<Action>();

        var window = new Window
        {
            Title = "Componentes del proyecto",
            Owner = owner,
            Width = 700,
            Height = 740,
            MinHeight = 420,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        TitleBar.Apply(window);

        var stack = new StackPanel { Margin = new Thickness(22, 18, 22, 8) };
        stack.Children.Add(new TextBlock { Text = "¿Qué tiene este proyecto?", FontSize = 18, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock
        {
            Text = "Activa o desactiva cada parte y ajusta sus datos. Lo que desactives desaparece del panel (puedes volver a activarlo cuando quieras).",
            Foreground = P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16),
        });

        TextBox Field(StackPanel panel, string label, string? val, Action<string> put, string? help = null)
        {
            panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 3) });
            var box = new TextBox { Text = val ?? "", Margin = new Thickness(0, 0, 0, 10), ToolTip = help };
            panel.Children.Add(box);
            apply.Add(() => put(box.Text.Trim()));
            return box;
        }

        ToggleButton Section(string title, string description, bool active, Action<StackPanel> fields)
        {
            var body = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Visibility = active ? Visibility.Visible : Visibility.Collapsed };
            fields(body);

            var toggle = new ToggleButton
            {
                Style = (Style)Application.Current.FindResource("ChipToggle"),
                IsChecked = active,
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            void Text() => toggle.Content = toggle.IsChecked == true ? "Activado" : "Desactivado";
            Text();
            toggle.Checked += (_, _) => { Text(); body.Visibility = Visibility.Visible; };
            toggle.Unchecked += (_, _) => { Text(); body.Visibility = Visibility.Collapsed; };

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
            texts.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 12, 0) });
            Grid.SetColumn(toggle, 1);
            header.Children.Add(texts);
            header.Children.Add(toggle);

            var content = new StackPanel();
            content.Children.Add(header);
            content.Children.Add(body);
            stack.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Card"), Margin = new Thickness(0, 0, 0, 12), Child = content });
            return toggle;
        }

        // Botón «Sugerir un puerto libre» bajo el campo URL de la API y de la web
        void SuggestUrl(StackPanel panel, TextBox boxUrl, ProjectComponent comp)
        {
            var warning = new TextBlock { FontSize = 11, Foreground = P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
            var button = new Button { Content = "Sugerir un puerto libre", Style = (Style)Application.Current.FindResource("BtnChip"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, -4, 0, 6) };
            button.Click += (_, _) =>
            {
                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    var m = System.Text.RegularExpressions.Regex.Match(boxUrl.Text, @"(://[^/:]+):(\d+)");
                    warning.Visibility = Visibility.Visible;
                    if (!m.Success) { warning.Text = "La URL necesita un puerto, p. ej. http://localhost:5109"; return; }
                    var current = int.Parse(m.Groups[2].Value);
                    var map = s.Free.Take(ignoreComponent: comp);
                    var reason = map.ReservedReason(current);
                    if (reason is null) { warning.Text = $"El puerto {current} no choca con otros proyectos, contenedores ni reservas de Windows."; return; }
                    var free = map.Suggest(current + 1);
                    if (free is null) { warning.Text = $"El puerto {current}: {reason}. No encontré uno libre cerca."; return; }
                    boxUrl.Text = boxUrl.Text.Remove(m.Groups[2].Index, m.Groups[2].Length).Insert(m.Groups[2].Index, free.Value.ToString());
                    warning.Text = $"El puerto {current}: {reason}. Puse {free}. Si tus comandos o variables de entorno usan {current}, cámbialos también (Diagnóstico → «Asignar puertos libres» lo hace solo).";
                }
                finally { Mouse.OverrideCursor = null; }
            };
            panel.Children.Add(button);
            panel.Children.Add(warning);
        }

        var tPg = Section("Postgres (Docker)", "Base de datos en un contenedor que la herramienta crea e inicia.", alreadyPg, c =>
        {
            Field(c, "Nombre del contenedor", pg.Container, v => pg.Container = Null(v));
            Field(c, "Imagen", pg.Image, v => pg.Image = Null(v), "p. ej. postgres:17-alpine");
            var boxPort = Field(c, "Puerto", pg.Port?.ToString(), v => { if (int.TryParse(v, out var n) && n > 0) pg.Port = n; });
            var warningPort = new TextBlock { FontSize = 11, Foreground = P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
            var suggest = new Button { Content = "Sugerir un puerto libre", Style = (Style)Application.Current.FindResource("BtnChip"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, -4, 0, 6) };
            suggest.Click += (_, _) =>
            {
                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    int.TryParse(boxPort.Text.Trim(), out var current);
                    var map = s.Free.Take(ignoreContainer: pg.Container, ignoreComponent: ProjectComponent.Postgres);
                    var reason = map.Conflict(current);
                    var free = map.Suggest(reason is null && current > 0 ? current : Math.Max(current, 5432) + (reason is null ? 0 : 1));
                    warningPort.Visibility = Visibility.Visible;
                    if (reason is null) warningPort.Text = $"El puerto {current} está libre: no choca con procesos, contenedores ni otros proyectos.";
                    else if (free is null) warningPort.Text = $"El puerto {current} {reason}, y no encontré uno libre cerca.";
                    else { boxPort.Text = free.Value.ToString(); warningPort.Text = $"El puerto {current} {reason}. Puse {free}, que está libre."; }
                }
                finally { Mouse.OverrideCursor = null; }
            };
            c.Children.Add(suggest);
            c.Children.Add(warningPort);
            Field(c, "Usuario", pg.User, v => pg.User = Null(v));
            Field(c, "Contraseña", pg.Password, v => pg.Password = v);
            Field(c, "Base de datos", pg.Database, v => pg.Database = Null(v));
            Field(c, "Volumen (vacío = sin volumen con nombre)", pg.Volume, v => pg.Volume = Null(v));
        });

        var tApi = Section("API .NET", "Servicio que se inicia con «dotnet run» (o el comando que pongas).", alreadyApi, c =>
        {
            Field(c, "Carpeta (relativa al proyecto)", api.Folder, v => api.Folder = Null(v));
            var boxUrlApi = Field(c, "URL", api.Url, v => api.Url = Null(v), "Sirve para saber si está corriendo (el puerto) y para abrirla en el navegador");
            SuggestUrl(c, boxUrlApi, ProjectComponent.Api);
            Field(c, "Ruta que se abre en el navegador", api.Open, v => api.Open = Null(v), "p. ej. /scalar");
            Field(c, "Comando para iniciar", api.Start, v => api.Start = Null(v));
            Field(c, "Comando de tests", api.Test, v => api.Test = Null(v));
            Field(c, "Comando de build", api.Build, v => api.Build = Null(v));
            c.Children.Add(new TextBlock
            {
                Text = "Variables de entorno de la API (una por línea: NOMBRE=valor). Puedes pegar líneas de PowerShell tal cual: $env:NOMBRE = \"valor\"",
                FontSize = 11, Foreground = P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3),
            });
            var envBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                MinHeight = 90,
                MaxHeight = 190,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
                FontSize = 12,
                Text = string.Join("\n", (api.Env ?? new Dictionary<string, string>()).Select(kv => $"{kv.Key}={kv.Value}")),
            };
            c.Children.Add(envBox);
            apply.Add(() => { var d = ParseEnv(envBox.Text); api.Env = d.Count == 0 ? null : d; });

            var text = new Button
            {
                Content = "Agregar cadena de conexión de Postgres",
                Style = (Style)Application.Current.FindResource("BtnChip"),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            text.Click += (_, _) =>
            {
                if (tPg.IsChecked != true)
                {
                    MessageBox.Show(window, "Activa primero Postgres.", "stackdev", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                foreach (var a in apply) a();   // pasa lo escrito en los campos a los objetos
                var suggested = detected.ConnectionName ?? (slug.Length > 0 ? char.ToUpperInvariant(slug[0]) + slug[1..] : "Default");
                var name = Dialogs.Ask(window, "Nombre de la cadena de conexión (ConnectionStrings__...)", suggested);
                if (name is null) return;
                var prefix = $"ConnectionStrings__{name}=";
                var lines = envBox.Text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0 && !l.StartsWith(prefix)).ToList();
                lines.Add(prefix + Detector.ConnectionString(pg));
                envBox.Text = string.Join("\n", lines);
            };
            c.Children.Add(text);
        });

        var tWeb = Section("Web (Angular)", "Aplicación web con pnpm/npm.", alreadyWeb, c =>
        {
            Field(c, "Carpeta (relativa al proyecto)", web.Folder, v => web.Folder = Null(v));
            var boxUrlWeb = Field(c, "URL", web.Url, v => web.Url = Null(v));
            SuggestUrl(c, boxUrlWeb, ProjectComponent.Web);
            Field(c, "Comando para iniciar", web.Start, v => web.Start = Null(v));
            Field(c, "Comando de tests", web.Test, v => web.Test = Null(v));
            Field(c, "Comando de build", web.Build, v => web.Build = Null(v));
            Field(c, "Comando de instalar dependencias", web.Install, v => web.Install = Null(v));
        });

        var tMobile = Section("Móvil (Flutter + emulador Android)", "App Flutter y el emulador donde se ejecuta.", alreadyMobile, c =>
        {
            Field(c, "Carpeta (relativa al proyecto)", mobile.Folder, v => mobile.Folder = Null(v));
            Field(c, "Android SDK (vacío = buscarlo solo)", mobile.AndroidSdk, v => mobile.AndroidSdk = Null(v));
            Field(c, "Emulador (AVD) por defecto", mobile.Avd, v => mobile.Avd = Null(v));
            Field(c, "Argumentos de flutter run", mobile.ArgsRun, v => mobile.ArgsRun = Null(v));
            Field(c, "Comando de análisis", mobile.Parse, v => mobile.Parse = Null(v));
            Field(c, "Comando de tests", mobile.Test, v => mobile.Test = Null(v));
            Field(c, "Comando de formato", mobile.Format, v => mobile.Format = Null(v));
            Field(c, "Comando de generación de código", mobile.Generate, v => mobile.Generate = Null(v));
        });

        var save = new Button { Content = "Guardar", Style = (Style)Application.Current.FindResource("BtnPrimary"), MinWidth = 100, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancelar", MinWidth = 90, Padding = new Thickness(16, 9, 16, 9), IsCancel = true };
        var saved = false;
        save.Click += (_, _) => { saved = true; window.Close(); };
        cancel.Click += (_, _) => window.Close();

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(22, 10, 22, 16),
        };
        footer.Children.Add(cancel);
        footer.Children.Add(save);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
        Grid.SetRow(footer, 1);
        root.Children.Add(scroll);
        root.Children.Add(footer);
        window.Content = root;

        window.ShowDialog();
        if (!saved) return null;

        foreach (var a in apply) a();

        cfg.Postgres = tPg.IsChecked == true ? pg : null;
        cfg.Api = tApi.IsChecked == true ? api : null;
        cfg.Web = tWeb.IsChecked == true ? web : null;
        cfg.Mobile = tMobile.IsChecked == true ? mobile : null;

        // La cadena de conexión de la API se agrega cuando se activa Postgres y ya hay API.
        if (cfg.Postgres is not null && !alreadyPg && cfg.Api is not null)
        {
            var before = cfg.StartAll;
            Detector.ApplyPostgres(cfg, cfg.Postgres, detected.ConnectionName ?? "Default");
            cfg.StartAll = before;
        }

        // «Levantar todo»: quitar lo desactivado y agregar lo recién activado.
        var byDefault = Detector.DefaultStartAll(cfg);
        if (cfg.StartAll is null) cfg.StartAll = byDefault;
        else
        {
            var list = cfg.StartAll.Where(n => byDefault.Contains(n)).ToList();
            void Fresh(bool already, bool now, string name) { if (!already && now && !list.Contains(name)) list.Add(name); }
            Fresh(alreadyPg, cfg.Postgres is not null, "postgres");
            Fresh(alreadyApi, cfg.Api is not null, "api");
            Fresh(alreadyWeb, cfg.Web is not null, "web");
            Fresh(alreadyMobile, cfg.Mobile is not null, "emulador");
            cfg.StartAll = list;
        }
        return cfg;
    }
}
