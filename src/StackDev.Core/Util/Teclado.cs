namespace StackDev.Core.Util;

public static class Teclado
{
    /// <summary>¿Se pulsó la tecla C? (las esperas largas se pueden cancelar con C o con Ctrl+C). No bloquea.</summary>
    public static bool PulsoCancelar()
    {
        try
        {
            if (Console.IsInputRedirected) return false;
            while (Console.KeyAvailable)
            {
                if (Console.ReadKey(true).Key == ConsoleKey.C) return true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        return false;
    }
}
