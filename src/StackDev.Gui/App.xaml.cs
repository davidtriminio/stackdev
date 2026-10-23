using System.IO;
using System;
using System.Threading;
using System.Windows;

namespace StackDev.Gui;

public partial class App : Application
{
    private const string InstanceName = @"Local\stackdev-gui-single-instance";
    private const string ShowSignalName = @"Local\stackdev-gui-show";
    private Mutex? _instance;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only one window of stackdev at a time: a second launch brings the first one to the front and exits.
        _instance = new Mutex(initiallyOwned: true, InstanceName, out var first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        if (!first)
        {
            try { _showSignal.Set(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }
        ListenForOtherLaunches();

        try { Appearance.Apply(StackDev.Core.State.StateStore.Open().Accent); } catch { }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instance?.ReleaseMutex(); } catch (ApplicationException) { }
        _instance?.Dispose();
        _showSignal?.Dispose();
        base.OnExit(e);
    }

    private void ListenForOtherLaunches()
    {
        var signal = _showSignal!;
        var thread = new Thread(() =>
        {
            while (true)
            {
                try { signal.WaitOne(); }
                catch (ObjectDisposedException) { return; }
                try { Dispatcher.Invoke(BringToFront); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Threading.Tasks.TaskCanceledException) { return; }
            }
        }) { IsBackground = true, Name = "stackdev-single-instance" };
        thread.Start();
    }

    private void BringToFront()
    {
        if (this.MainWindow is not { } w) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        // Windows may refuse to steal focus; toggling Topmost reliably raises the window.
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
    }

    public App()
    {
        // Un error inesperado no debe cerrar la ventana en silencio.
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "stackdev", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}
