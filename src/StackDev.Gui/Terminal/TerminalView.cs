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
/// A terminal: xterm.js inside a WebView2 as the screen and a <see cref="SessionPty"/> (ConPTY) as the program.
/// Copy and paste go through the Windows clipboard (Ctrl+C with a selection, Ctrl+V, right click).
/// </summary>
public sealed class TerminalView : Grid, IDisposable
{
    private static Task<CoreWebView2Environment>? _environment;
    private static string? _html;

    private readonly WebView2 _web;
    private readonly object _lock = new();
    private readonly StringBuilder _pending = new();
    private int _scheduled;
    private bool _started;
    private bool _closed;
    private SessionPty? _pty;

    public string CommandLine { get; }
    public string Folder { get; }
    private readonly IReadOnlyDictionary<string, string>? _env;

    /// <summary>The program ended (with its exit code).</summary>
    public event Action<TerminalView, int>? Finished;

    /// <summary>The program changed the terminal title (OSC sequence).</summary>
    public event Action<TerminalView, string>? TitleChanged;

    /// <summary>An application shortcut pressed with focus inside the terminal (next, prev, toggle, new, tab).</summary>
    public event Action<string, int>? Shortcut;

    public TerminalView(string line, string folder, IReadOnlyDictionary<string, string>? env)
    {
        CommandLine = line;
        Folder = folder;
        _env = env;
        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x15));
        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x08, 0x09, 0x0A) };
        Children.Add(_web);
        Loaded += (_, _) =>
        {
            if (_started) return;
            _started = true;
            _ = StartAsync();
        };
    }

    // ------------------------------------------------------------ WebView2

    private static Task<CoreWebView2Environment> CreateWebViewEnvironment() =>
        _environment ??= CoreWebView2Environment.CreateAsync(null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stackdev", "webview2"));

    private static string Html()
    {
        if (_html is not null) return _html;
        using var s = typeof(TerminalView).Assembly.GetManifestResourceStream("terminal.html")
                      ?? throw new InvalidOperationException("Falta el recurso terminal.html en el ejecutable.");
        using var r = new StreamReader(s, Encoding.UTF8);
        return _html = r.ReadToEnd();
    }

    private async Task StartAsync()
    {
        try
        {
            var environment = await CreateWebViewEnvironment();
            await _web.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            var c = _web.CoreWebView2;
            c.Settings.AreDefaultContextMenusEnabled = false;
            c.Settings.AreDevToolsEnabled = false;
            c.Settings.AreBrowserAcceleratorKeysEnabled = false;
            c.Settings.IsStatusBarEnabled = false;
            c.Settings.IsZoomControlEnabled = false;
            c.Settings.IsBuiltInErrorPageEnabled = false;
            c.WebMessageReceived += OnMessageReceived;
            c.NavigateToString(Html());
        }
        catch (Exception ex)
        {
            _environment = null;
            ShowError(ex is WebView2RuntimeNotFoundException
                ? "Falta el runtime de WebView2. Instálalo desde https://developer.microsoft.com/microsoft-edge/webview2/ y reabre la aplicación."
                : $"No se pudo iniciar la terminal: {ex.Message}");
        }
    }

    private void ShowError(string text)
    {
        Children.Clear();
        Children.Add(new TextBlock
        {
            Text = text,
            Foreground = (Brush)Application.Current.FindResource("ErrBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18),
        });
    }

    private void Send(object message)
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception) { }
    }

    // ------------------------------------------------------------ Messages from the page

    private void OnMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(e.WebMessageAsJson); }
        catch (JsonException) { return; }
        using (doc)
        {
            var r = doc.RootElement;
            if (!r.TryGetProperty("t", out var type)) return;
            switch (type.GetString())
            {
                case "ready": Start(r.GetProperty("c").GetInt32(), r.GetProperty("r").GetInt32()); break;
                case "i": _pty?.Write(r.GetProperty("d").GetString() ?? ""); break;
                case "r": _pty?.Resize(r.GetProperty("c").GetInt32(), r.GetProperty("r").GetInt32()); break;
                case "title": TitleChanged?.Invoke(this, r.GetProperty("d").GetString() ?? ""); break;
                case "copy": CopyToClipboard(r.GetProperty("d").GetString()); break;
                case "pasteReq": Paste(); break;
                case "key": Shortcut?.Invoke(r.GetProperty("d").GetString() ?? "", r.TryGetProperty("n", out var nn) ? nn.GetInt32() : 0); break;
            }
        }
    }

    private void Start(int columns, int rows)
    {
        if (_pty is not null || _closed) return;
        try
        {
            var pty = SessionPty.Start(CommandLine, Folder, _env, Math.Max(columns, 20), Math.Max(rows, 5));
            pty.Output += OnOutputArrived;
            pty.Finished += code => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_closed) Finished?.Invoke(this, code);
            }));
            _pty = pty;
            if (IsVisible) Focus();
        }
        catch (Exception ex)
        {
            Send(new { t = "o", d = $"\r\n\u001b[31mNo se pudo iniciar el shell: {ex.Message}\u001b[0m\r\n" });
        }
    }

    private static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (ExternalException) { }   // the clipboard is held by another program at this instant
    }

    private void Paste()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (ExternalException) { return; }
        if (text.Length > 0) Send(new { t = "paste", d = text });
    }

    // ------------------------------------------------------------ Output to the page

    /// <summary>Called from the reading thread: gathers what arrives and sends it to the page in batches, without flooding the interface.</summary>
    private void OnOutputArrived(string text)
    {
        lock (_lock) _pending.Append(text);
        if (Interlocked.Exchange(ref _scheduled, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Empty));
    }

    private void Empty()
    {
        string text;
        lock (_lock)
        {
            var n = Math.Min(_pending.Length, 65536);
            if (n > 0 && n < _pending.Length && char.IsHighSurrogate(_pending[n - 1])) n--;   // do not split a surrogate pair
            text = _pending.ToString(0, n);
            _pending.Remove(0, n);
        }
        Interlocked.Exchange(ref _scheduled, 0);

        if (text.Length > 0 && !_closed) Send(new { t = "o", d = text });

        bool remainsMore;
        lock (_lock) remainsMore = _pending.Length > 0;
        if (remainsMore && !_closed && Interlocked.Exchange(ref _scheduled, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Empty));
    }

    // ------------------------------------------------------------ Uso

    public void Focus()
    {
        if (_closed) return;
        _web.Focus();
        Send(new { t = "focus" });
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _pty?.Dispose();
        try { _web.Dispose(); } catch (Exception) { }
    }
}
