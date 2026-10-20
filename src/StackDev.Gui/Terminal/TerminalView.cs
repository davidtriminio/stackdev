using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace StackDev.Gui.Terminal;

/// <summary>
/// Una terminal: xterm.js dentro de un WebView2 como pantalla y un <see cref="SesionPty"/> (ConPTY) como programa.
/// Copiar y pegar pasan por el portapapeles de Windows (Ctrl+C con selección, Ctrl+V, clic derecho).
/// </summary>
public sealed class TerminalView : Grid, IDisposable
{
    private static Task<CoreWebView2Environment>? _entorno;
    private static string? _html;

    private readonly WebView2 _web;
    private readonly object _bloqueo = new();
    private readonly StringBuilder _pendiente = new();
    private int _programado;
    private bool _iniciado;
    private bool _cerrado;
    private SesionPty? _pty;

    public string Linea { get; }
    public string Carpeta { get; }
    private readonly IReadOnlyDictionary<string, string>? _env;

    /// <summary>El programa terminó (con su código de salida).</summary>
    public event Action<TerminalView, int>? Terminado;

    /// <summary>El programa cambió el título de la terminal (secuencia OSC).</summary>
    public event Action<TerminalView, string>? TituloCambiado;

    /// <summary>Un atajo de la aplicación pulsado con el foco dentro de la terminal (next, prev, toggle, new, tab).</summary>
    public event Action<string, int>? Atajo;

    public TerminalView(string linea, string carpeta, IReadOnlyDictionary<string, string>? env)
    {
        Linea = linea;
        Carpeta = carpeta;
        _env = env;
        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x15));
        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x08, 0x09, 0x0A) };
        Children.Add(_web);
        Loaded += (_, _) =>
        {
            if (_iniciado) return;
            _iniciado = true;
            _ = IniciarAsync();
        };
    }

    // ------------------------------------------------------------ WebView2

    private static Task<CoreWebView2Environment> Entorno() =>
        _entorno ??= CoreWebView2Environment.CreateAsync(null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stackdev", "webview2"));

    private static string Html()
    {
        if (_html is not null) return _html;
        using var s = typeof(TerminalView).Assembly.GetManifestResourceStream("terminal.html")
                      ?? throw new InvalidOperationException("Falta el recurso terminal.html en el ejecutable.");
        using var r = new StreamReader(s, Encoding.UTF8);
        return _html = r.ReadToEnd();
    }

    private async Task IniciarAsync()
    {
        try
        {
            var entorno = await Entorno();
            await _web.EnsureCoreWebView2Async(entorno);
            if (_cerrado) return;
            var c = _web.CoreWebView2;
            c.Settings.AreDefaultContextMenusEnabled = false;
            c.Settings.AreDevToolsEnabled = false;
            c.Settings.AreBrowserAcceleratorKeysEnabled = false;
            c.Settings.IsStatusBarEnabled = false;
            c.Settings.IsZoomControlEnabled = false;
            c.Settings.IsBuiltInErrorPageEnabled = false;
            c.WebMessageReceived += AlRecibirMensaje;
            c.NavigateToString(Html());
        }
        catch (Exception ex)
        {
            _entorno = null;
            MostrarError(ex is WebView2RuntimeNotFoundException
                ? "Falta el runtime de WebView2. Instálalo desde https://developer.microsoft.com/microsoft-edge/webview2/ y reabre la aplicación."
                : $"No se pudo iniciar la terminal: {ex.Message}");
        }
    }

    private void MostrarError(string texto)
    {
        Children.Clear();
        Children.Add(new TextBlock
        {
            Text = texto,
            Foreground = (Brush)Application.Current.FindResource("ErrBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18),
        });
    }

    private void Enviar(object mensaje)
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(mensaje)); }
        catch (Exception) { }
    }

    // ------------------------------------------------------------ Mensajes de la página

    private void AlRecibirMensaje(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(e.WebMessageAsJson); }
        catch (JsonException) { return; }
        using (doc)
        {
            var r = doc.RootElement;
            if (!r.TryGetProperty("t", out var tipo)) return;
            switch (tipo.GetString())
            {
                case "ready": Arrancar(r.GetProperty("c").GetInt32(), r.GetProperty("r").GetInt32()); break;
                case "i": _pty?.Escribir(r.GetProperty("d").GetString() ?? ""); break;
                case "r": _pty?.Redimensionar(r.GetProperty("c").GetInt32(), r.GetProperty("r").GetInt32()); break;
                case "title": TituloCambiado?.Invoke(this, r.GetProperty("d").GetString() ?? ""); break;
                case "copy": CopiarAlPortapapeles(r.GetProperty("d").GetString()); break;
                case "pasteReq": Pegar(); break;
                case "key": Atajo?.Invoke(r.GetProperty("d").GetString() ?? "", r.TryGetProperty("n", out var nn) ? nn.GetInt32() : 0); break;
            }
        }
    }

    private void Arrancar(int columnas, int filas)
    {
        if (_pty is not null || _cerrado) return;
        try
        {
            var pty = SesionPty.Iniciar(Linea, Carpeta, _env, Math.Max(columnas, 20), Math.Max(filas, 5));
            pty.Salida += AlLlegarSalida;
            pty.Terminado += codigo => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_cerrado) Terminado?.Invoke(this, codigo);
            }));
            _pty = pty;
            if (IsVisible) Enfocar();
        }
        catch (Exception ex)
        {
            Enviar(new { t = "o", d = $"\r\n\u001b[31mNo se pudo iniciar el shell: {ex.Message}\u001b[0m\r\n" });
        }
    }

    private static void CopiarAlPortapapeles(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return;
        try { Clipboard.SetText(texto); }
        catch (ExternalException) { }   // el portapapeles lo tiene otro programa en este instante
    }

    private void Pegar()
    {
        string texto;
        try { texto = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (ExternalException) { return; }
        if (texto.Length > 0) Enviar(new { t = "paste", d = texto });
    }

    // ------------------------------------------------------------ Salida hacia la página

    /// <summary>Se llama desde el hilo de lectura: junta lo que llega y lo manda a la página en tandas, sin saturar la interfaz.</summary>
    private void AlLlegarSalida(string texto)
    {
        lock (_bloqueo) _pendiente.Append(texto);
        if (Interlocked.Exchange(ref _programado, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Vaciar));
    }

    private void Vaciar()
    {
        string texto;
        lock (_bloqueo)
        {
            var n = Math.Min(_pendiente.Length, 65536);
            if (n > 0 && n < _pendiente.Length && char.IsHighSurrogate(_pendiente[n - 1])) n--;   // no partir un par sustituto
            texto = _pendiente.ToString(0, n);
            _pendiente.Remove(0, n);
        }
        Interlocked.Exchange(ref _programado, 0);

        if (texto.Length > 0 && !_cerrado) Enviar(new { t = "o", d = texto });

        bool quedaMas;
        lock (_bloqueo) quedaMas = _pendiente.Length > 0;
        if (quedaMas && !_cerrado && Interlocked.Exchange(ref _programado, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Vaciar));
    }

    // ------------------------------------------------------------ Uso

    public void Enfocar()
    {
        if (_cerrado) return;
        _web.Focus();
        Enviar(new { t = "focus" });
    }

    public void Dispose()
    {
        if (_cerrado) return;
        _cerrado = true;
        _pty?.Dispose();
        try { _web.Dispose(); } catch (Exception) { }
    }
}
