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
public static class ComponentesDialog
{
    private static Brush P(string clave) => (Brush)Application.Current.FindResource(clave);

    /// <summary>
    /// Entiende NOMBRE=valor, $env:NOMBRE = "valor" y set NOMBRE=valor. Lo que no parezca una variable (un comando, por ejemplo) se ignora.
    /// </summary>
    public static Dictionary<string, string> ParsearEnv(string texto)
    {
        var res = new Dictionary<string, string>();
        foreach (var cruda in texto.Split('\n'))
        {
            var l = cruda.Trim().TrimEnd(';');
            if (l.Length == 0 || l.StartsWith('#')) continue;
            if (l.StartsWith("$env:", StringComparison.OrdinalIgnoreCase)) l = l[5..];
            else if (l.StartsWith("set ", StringComparison.OrdinalIgnoreCase)) l = l[4..].TrimStart();
            var i = l.IndexOf('=');
            if (i <= 0) continue;
            var nombre = l[..i].Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(nombre, @"^[A-Za-z_][A-Za-z0-9_]*$")) continue;
            var valor = l[(i + 1)..].Trim();
            if (valor.Length >= 2 && (valor[0] == '"' || valor[0] == '\'') && valor[^1] == valor[0]) valor = valor[1..^1];
            res[nombre] = valor;
        }
        return res;
    }

    private static string? Nulo(string t) => t.Length == 0 ? null : t;

    public static ProyectoConfig? Mostrar(Window owner, Sesion s)
    {
        var cfg = ConfigJson.Leer(ConfigJson.Escribir(s.Ctx.Config));   // copia de trabajo
        var detectado = Detector.Detectar(s.Raiz);
        var slug = Rutas.Slug(Rutas.NombreCarpeta(s.Raiz));

        var yaPg = cfg.Postgres is not null;
        var yaApi = cfg.Api is not null;
        var yaWeb = cfg.Web is not null;
        var yaMovil = cfg.Movil is not null;

        // Si está desactivado se muestran valores sugeridos (lo detectado o unos por defecto) por si lo activan.
        var pg = cfg.Postgres ?? new PostgresConfig
        {
            Contenedor = slug + "-db", Imagen = "postgres:17-alpine", Puerto = 5432,
            Usuario = "postgres", Password = "postgres", BaseDatos = slug, Volumen = slug + "-pgdata",
        };
        var api = cfg.Api ?? detectado.Config.Api ?? new ApiConfig
        {
            Carpeta = ".", Url = "http://localhost:5000", Iniciar = "dotnet run", Test = "dotnet test", Build = "dotnet build",
        };
        var web = cfg.Web ?? detectado.Config.Web ?? new WebConfig
        {
            Carpeta = ".", Url = "http://localhost:4200", Iniciar = "pnpm start", Test = "pnpm test", Build = "pnpm build", Instalar = "pnpm install",
        };
        var movil = cfg.Movil ?? detectado.Config.Movil ?? new MovilConfig
        {
            Carpeta = ".", Analizar = "flutter analyze", Test = "flutter test", Formato = "dart format .",
            Generar = "dart run build_runner build --delete-conflicting-outputs",
        };

        var aplicar = new List<Action>();

        var ventana = new Window
        {
            Title = "Componentes del proyecto",
            Owner = owner,
            Width = 700,
            Height = 740,
            MinHeight = 420,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        BarraTitulo.Aplicar(ventana);

        var pila = new StackPanel { Margin = new Thickness(22, 18, 22, 8) };
        pila.Children.Add(new TextBlock { Text = "¿Qué tiene este proyecto?", FontSize = 18, FontWeight = FontWeights.SemiBold });
        pila.Children.Add(new TextBlock
        {
            Text = "Activa o desactiva cada parte y ajusta sus datos. Lo que desactives desaparece del panel (puedes volver a activarlo cuando quieras).",
            Foreground = P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16),
        });

        TextBox Campo(StackPanel panel, string etiqueta, string? valor, Action<string> poner, string? ayuda = null)
        {
            panel.Children.Add(new TextBlock { Text = etiqueta, FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 3) });
            var caja = new TextBox { Text = valor ?? "", Margin = new Thickness(0, 0, 0, 10), ToolTip = ayuda };
            panel.Children.Add(caja);
            aplicar.Add(() => poner(caja.Text.Trim()));
            return caja;
        }

        ToggleButton Seccion(string titulo, string descripcion, bool activo, Action<StackPanel> campos)
        {
            var cuerpo = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Visibility = activo ? Visibility.Visible : Visibility.Collapsed };
            campos(cuerpo);

            var interruptor = new ToggleButton
            {
                Style = (Style)Application.Current.FindResource("ChipToggle"),
                IsChecked = activo,
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            void Texto() => interruptor.Content = interruptor.IsChecked == true ? "Activado" : "Desactivado";
            Texto();
            interruptor.Checked += (_, _) => { Texto(); cuerpo.Visibility = Visibility.Visible; };
            interruptor.Unchecked += (_, _) => { Texto(); cuerpo.Visibility = Visibility.Collapsed; };

            var cabecera = new Grid();
            cabecera.ColumnDefinitions.Add(new ColumnDefinition());
            cabecera.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var textos = new StackPanel();
            textos.Children.Add(new TextBlock { Text = titulo, FontSize = 15, FontWeight = FontWeights.SemiBold });
            textos.Children.Add(new TextBlock { Text = descripcion, FontSize = 12, Foreground = P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 12, 0) });
            Grid.SetColumn(interruptor, 1);
            cabecera.Children.Add(textos);
            cabecera.Children.Add(interruptor);

            var contenido = new StackPanel();
            contenido.Children.Add(cabecera);
            contenido.Children.Add(cuerpo);
            pila.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Card"), Margin = new Thickness(0, 0, 0, 12), Child = contenido });
            return interruptor;
        }

        // Botón «Sugerir un puerto libre» bajo el campo URL de la API y de la web
        void SugerirUrl(StackPanel panel, TextBox cajaUrl, Componente comp)
        {
            var aviso = new TextBlock { FontSize = 11, Foreground = P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
            var boton = new Button { Content = "Sugerir un puerto libre", Style = (Style)Application.Current.FindResource("BtnChip"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, -4, 0, 6) };
            boton.Click += (_, _) =>
            {
                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    var m = System.Text.RegularExpressions.Regex.Match(cajaUrl.Text, @"(://[^/:]+):(\d+)");
                    aviso.Visibility = Visibility.Visible;
                    if (!m.Success) { aviso.Text = "La URL necesita un puerto, p. ej. http://localhost:5109"; return; }
                    var actual = int.Parse(m.Groups[2].Value);
                    var mapa = s.Libres.Tomar(ignorarComponente: comp);
                    var motivo = mapa.Reservado(actual);
                    if (motivo is null) { aviso.Text = $"El puerto {actual} no choca con otros proyectos, contenedores ni reservas de Windows."; return; }
                    var libre = mapa.Sugerir(actual + 1);
                    if (libre is null) { aviso.Text = $"El puerto {actual}: {motivo}. No encontré uno libre cerca."; return; }
                    cajaUrl.Text = cajaUrl.Text.Remove(m.Groups[2].Index, m.Groups[2].Length).Insert(m.Groups[2].Index, libre.Value.ToString());
                    aviso.Text = $"El puerto {actual}: {motivo}. Puse {libre}. Si tus comandos o variables de entorno usan {actual}, cámbialos también (Diagnóstico → «Asignar puertos libres» lo hace solo).";
                }
                finally { Mouse.OverrideCursor = null; }
            };
            panel.Children.Add(boton);
            panel.Children.Add(aviso);
        }

        var tPg = Seccion("Postgres (Docker)", "Base de datos en un contenedor que la herramienta crea e inicia.", yaPg, c =>
        {
            Campo(c, "Nombre del contenedor", pg.Contenedor, v => pg.Contenedor = Nulo(v));
            Campo(c, "Imagen", pg.Imagen, v => pg.Imagen = Nulo(v), "p. ej. postgres:17-alpine");
            var cajaPuerto = Campo(c, "Puerto", pg.Puerto?.ToString(), v => { if (int.TryParse(v, out var n) && n > 0) pg.Puerto = n; });
            var avisoPuerto = new TextBlock { FontSize = 11, Foreground = P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
            var sugerir = new Button { Content = "Sugerir un puerto libre", Style = (Style)Application.Current.FindResource("BtnChip"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, -4, 0, 6) };
            sugerir.Click += (_, _) =>
            {
                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    int.TryParse(cajaPuerto.Text.Trim(), out var actual);
                    var mapa = s.Libres.Tomar(ignorarContenedor: pg.Contenedor, ignorarComponente: Componente.Postgres);
                    var motivo = mapa.Conflicto(actual);
                    var libre = mapa.Sugerir(motivo is null && actual > 0 ? actual : Math.Max(actual, 5432) + (motivo is null ? 0 : 1));
                    avisoPuerto.Visibility = Visibility.Visible;
                    if (motivo is null) avisoPuerto.Text = $"El puerto {actual} está libre: no choca con procesos, contenedores ni otros proyectos.";
                    else if (libre is null) avisoPuerto.Text = $"El puerto {actual} {motivo}, y no encontré uno libre cerca.";
                    else { cajaPuerto.Text = libre.Value.ToString(); avisoPuerto.Text = $"El puerto {actual} {motivo}. Puse {libre}, que está libre."; }
                }
                finally { Mouse.OverrideCursor = null; }
            };
            c.Children.Add(sugerir);
            c.Children.Add(avisoPuerto);
            Campo(c, "Usuario", pg.Usuario, v => pg.Usuario = Nulo(v));
            Campo(c, "Contraseña", pg.Password, v => pg.Password = v);
            Campo(c, "Base de datos", pg.BaseDatos, v => pg.BaseDatos = Nulo(v));
            Campo(c, "Volumen (vacío = sin volumen con nombre)", pg.Volumen, v => pg.Volumen = Nulo(v));
        });

        var tApi = Seccion("API .NET", "Servicio que se inicia con «dotnet run» (o el comando que pongas).", yaApi, c =>
        {
            Campo(c, "Carpeta (relativa al proyecto)", api.Carpeta, v => api.Carpeta = Nulo(v));
            var cajaUrlApi = Campo(c, "URL", api.Url, v => api.Url = Nulo(v), "Sirve para saber si está corriendo (el puerto) y para abrirla en el navegador");
            SugerirUrl(c, cajaUrlApi, Componente.Api);
            Campo(c, "Ruta que se abre en el navegador", api.Abrir, v => api.Abrir = Nulo(v), "p. ej. /scalar");
            Campo(c, "Comando para iniciar", api.Iniciar, v => api.Iniciar = Nulo(v));
            Campo(c, "Comando de tests", api.Test, v => api.Test = Nulo(v));
            Campo(c, "Comando de build", api.Build, v => api.Build = Nulo(v));
            c.Children.Add(new TextBlock
            {
                Text = "Variables de entorno de la API (una por línea: NOMBRE=valor). Puedes pegar líneas de PowerShell tal cual: $env:NOMBRE = \"valor\"",
                FontSize = 11, Foreground = P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3),
            });
            var envCaja = new TextBox
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
            c.Children.Add(envCaja);
            aplicar.Add(() => { var d = ParsearEnv(envCaja.Text); api.Env = d.Count == 0 ? null : d; });

            var cadena = new Button
            {
                Content = "Agregar cadena de conexión de Postgres",
                Style = (Style)Application.Current.FindResource("BtnChip"),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            cadena.Click += (_, _) =>
            {
                if (tPg.IsChecked != true)
                {
                    MessageBox.Show(ventana, "Activa primero Postgres.", "stackdev", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                foreach (var a in aplicar) a();   // pasa lo escrito en los campos a los objetos
                var sugerido = detectado.NombreConexion ?? (slug.Length > 0 ? char.ToUpperInvariant(slug[0]) + slug[1..] : "Default");
                var nombre = Dialogos.Preguntar(ventana, "Nombre de la cadena de conexión (ConnectionStrings__...)", sugerido);
                if (nombre is null) return;
                var prefijo = $"ConnectionStrings__{nombre}=";
                var lineas = envCaja.Text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0 && !l.StartsWith(prefijo)).ToList();
                lineas.Add(prefijo + Detector.CadenaConexion(pg));
                envCaja.Text = string.Join("\n", lineas);
            };
            c.Children.Add(cadena);
        });

        var tWeb = Seccion("Web (Angular)", "Aplicación web con pnpm/npm.", yaWeb, c =>
        {
            Campo(c, "Carpeta (relativa al proyecto)", web.Carpeta, v => web.Carpeta = Nulo(v));
            var cajaUrlWeb = Campo(c, "URL", web.Url, v => web.Url = Nulo(v));
            SugerirUrl(c, cajaUrlWeb, Componente.Web);
            Campo(c, "Comando para iniciar", web.Iniciar, v => web.Iniciar = Nulo(v));
            Campo(c, "Comando de tests", web.Test, v => web.Test = Nulo(v));
            Campo(c, "Comando de build", web.Build, v => web.Build = Nulo(v));
            Campo(c, "Comando de instalar dependencias", web.Instalar, v => web.Instalar = Nulo(v));
        });

        var tMovil = Seccion("Móvil (Flutter + emulador Android)", "App Flutter y el emulador donde se ejecuta.", yaMovil, c =>
        {
            Campo(c, "Carpeta (relativa al proyecto)", movil.Carpeta, v => movil.Carpeta = Nulo(v));
            Campo(c, "Android SDK (vacío = buscarlo solo)", movil.AndroidSdk, v => movil.AndroidSdk = Nulo(v));
            Campo(c, "Emulador (AVD) por defecto", movil.Avd, v => movil.Avd = Nulo(v));
            Campo(c, "Argumentos de flutter run", movil.ArgsRun, v => movil.ArgsRun = Nulo(v));
            Campo(c, "Comando de análisis", movil.Analizar, v => movil.Analizar = Nulo(v));
            Campo(c, "Comando de tests", movil.Test, v => movil.Test = Nulo(v));
            Campo(c, "Comando de formato", movil.Formato, v => movil.Formato = Nulo(v));
            Campo(c, "Comando de generación de código", movil.Generar, v => movil.Generar = Nulo(v));
        });

        var guardar = new Button { Content = "Guardar", Style = (Style)Application.Current.FindResource("BtnPrimary"), MinWidth = 100, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        var cancelar = new Button { Content = "Cancelar", MinWidth = 90, Padding = new Thickness(16, 9, 16, 9), IsCancel = true };
        var guardado = false;
        guardar.Click += (_, _) => { guardado = true; ventana.Close(); };
        cancelar.Click += (_, _) => ventana.Close();

        var pie = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(22, 10, 22, 16),
        };
        pie.Children.Add(cancelar);
        pie.Children.Add(guardar);

        var raiz = new Grid();
        raiz.RowDefinitions.Add(new RowDefinition());
        raiz.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = pila };
        Grid.SetRow(pie, 1);
        raiz.Children.Add(scroll);
        raiz.Children.Add(pie);
        ventana.Content = raiz;

        ventana.ShowDialog();
        if (!guardado) return null;

        foreach (var a in aplicar) a();

        cfg.Postgres = tPg.IsChecked == true ? pg : null;
        cfg.Api = tApi.IsChecked == true ? api : null;
        cfg.Web = tWeb.IsChecked == true ? web : null;
        cfg.Movil = tMovil.IsChecked == true ? movil : null;

        // La cadena de conexión de la API se agrega cuando se activa Postgres y ya hay API.
        if (cfg.Postgres is not null && !yaPg && cfg.Api is not null)
        {
            var antes = cfg.LevantarTodo;
            Detector.AplicarPostgres(cfg, cfg.Postgres, detectado.NombreConexion ?? "Default");
            cfg.LevantarTodo = antes;
        }

        // «Levantar todo»: quitar lo desactivado y agregar lo recién activado.
        var porDefecto = Detector.LevantarTodoPorDefecto(cfg);
        if (cfg.LevantarTodo is null) cfg.LevantarTodo = porDefecto;
        else
        {
            var lista = cfg.LevantarTodo.Where(n => porDefecto.Contains(n)).ToList();
            void Nuevo(bool ya, bool ahora, string nombre) { if (!ya && ahora && !lista.Contains(nombre)) lista.Add(nombre); }
            Nuevo(yaPg, cfg.Postgres is not null, "postgres");
            Nuevo(yaApi, cfg.Api is not null, "api");
            Nuevo(yaWeb, cfg.Web is not null, "web");
            Nuevo(yaMovil, cfg.Movil is not null, "emulador");
            cfg.LevantarTodo = lista;
        }
        return cfg;
    }
}
