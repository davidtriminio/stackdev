using System.Windows;

namespace StackDev.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try { Apariencia.Aplicar(StackDev.Core.State.StateStore.Abrir().Acento); } catch { }
        base.OnStartup(e);
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
