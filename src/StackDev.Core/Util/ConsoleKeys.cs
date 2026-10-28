namespace StackDev.Core.Util;

public static class ConsoleKeys
{
    /// <summary>Was the C key pressed? (long waits can be cancelled with C or with Ctrl+C). Does not block.</summary>
    public static bool PressCancel()
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
