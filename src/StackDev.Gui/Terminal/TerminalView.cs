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
    // Output waiting to go to the page, as the chunks the reading thread produced (dequeuing is O(1) however much there is).
    private readonly Queue<string> _chunks = new();
    private int _queued;      // characters in _chunks
    private int _inFlight;    // characters sent to the page that it has not drawn yet
    private int _scheduled;
    private volatile bool _shown = true;   // a hidden page runs slowly: it is not worth making the program wait for it

    private const int BatchChars = 64 * 1024;
    private const int MaxInFlight = 512 * 1024;     // more than this and the page gets time to catch up before it is sent more
    private const int MaxQueued = 4 * 1024 * 1024;  // more than this and the program is made to wait (the pipe fills up and slows it down)
    private const int SliceMs = 8;                  // the longest the interface thread is kept busy sending in one go
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
        IsVisibleChanged += (_, _) => _shown = IsVisible;
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
                case "a": OnPageDrew(r.GetProperty("n").GetInt32()); break;
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

    /// <summary>
    /// Called from the reading thread: queues what arrives. The interface thread sends it to the page in short slices, and
    /// only while the page keeps up; if the program writes faster than that for long, the reading thread waits here instead of
    /// piling up memory, which makes the program itself slow down.
    /// </summary>
    private void OnOutputArrived(string text)
    {
        lock (_lock) { _chunks.Enqueue(text); _queued += text.Length; }
        Schedule();
        if (Volatile.Read(ref _queued) <= MaxQueued) return;
        if (_shown)
        {
            while (Volatile.Read(ref _queued) > MaxQueued && !_closed && _shown) Thread.Sleep(10);
            return;
        }
        // Hidden terminal with a mountain of output: keep the latest part instead of stalling the program behind it.
        lock (_lock)
            while (_queued > MaxQueued / 2 && _chunks.Count > 1) _queued -= _chunks.Dequeue().Length;
    }

    private void Schedule()
    {
        if (_closed || Interlocked.Exchange(ref _scheduled, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Empty));
    }

    private void OnPageDrew(int chars)
    {
        Interlocked.Add(ref _inFlight, -chars);
        Schedule();
    }

    private void Empty()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!_closed && Volatile.Read(ref _inFlight) < MaxInFlight && clock.ElapsedMilliseconds < SliceMs)
        {
            string batch;
            lock (_lock)
            {
                if (_chunks.Count == 0) return;
                var sb = new StringBuilder();
                while (_chunks.Count > 0 && sb.Length < BatchChars)
                {
                    var chunk = _chunks.Dequeue();
                    sb.Append(chunk);
                    _queued -= chunk.Length;
                }
                batch = sb.ToString();
            }
            Interlocked.Add(ref _inFlight, batch.Length);
            Send(new { t = "o", d = batch });
        }
        // Stopped because the slice ran out: carry on right after letting the interface breathe. If it was the page that
        // is behind, its acknowledgement schedules the next slice.
        bool more;
        lock (_lock) more = _chunks.Count > 0;
        if (more && !_closed && Volatile.Read(ref _inFlight) < MaxInFlight) Schedule();
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
