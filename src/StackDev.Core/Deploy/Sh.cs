using System.Text.RegularExpressions;

namespace StackDev.Core.Deploy;

/// <summary>Quoting and validation for values that end up in a command line sent to the server.</summary>
public static class Sh
{
    /// <summary>Single-quotes a value for a POSIX shell.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public static readonly Regex Service = new("^[a-z][a-z0-9-]{0,39}$");
    public static readonly Regex Dll = new(@"^[A-Za-z][A-Za-z0-9._-]*\.dll$");
    public static readonly Regex Domain = new(@"^([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}$");
    public static readonly Regex Email = new(@"^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$");

    public static bool ValidPort(int port) => port is >= 1024 and <= 65535;
}
