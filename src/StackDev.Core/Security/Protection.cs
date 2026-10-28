using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace StackDev.Core.Security;

/// <summary>
/// Encryption of secrets with Windows DPAPI (<c>CryptProtectData</c>, current-user scope).
/// Only your Windows user, on this computer, can decrypt them: there is no key to store or lose.
/// Encrypted values are written as <c>dpapi:BASE64</c>; anything without that prefix is read as normal text
/// (so a hand-written or inherited file keeps working and is encrypted by itself the next time it is saved).
/// On systems other than Windows nothing is encrypted.
/// </summary>
public static class Protection
{
    public const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("stackdev/v1");

    public static bool Available => OperatingSystem.IsWindows();

    public static bool IsEncrypted(string? text) => text is not null && text.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Variable names that almost surely hold a secret (passwords, tokens, keys, connection strings).</summary>
    public static bool LooksSecret(string name) =>
        Regex.IsMatch(name, "PASSWORD|PASSWD|PWD|SECRET|TOKEN|APIKEY|API_KEY|PRIVATE|CREDENTIAL|CONNECTIONSTRING|CONNECTION_STRING|__KEY|_KEY$|^KEY$", RegexOptions.IgnoreCase);

    /// <summary>Encrypts a text; if it is already encrypted, empty or DPAPI is not available, returns it unchanged.</summary>
    public static string Encrypt(string? text)
    {
        if (string.IsNullOrEmpty(text) || IsEncrypted(text) || !Available) return text ?? "";
        try { return Prefix + Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(text), encrypt: true)); }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        { return text; }
    }

    /// <summary>Decrypts a <c>dpapi:…</c> text; if it is not encrypted returns it unchanged. If it cannot be (another user or computer), returns "".</summary>
    public static string Decrypt(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IsEncrypted(text)) return text ?? "";
        if (!Available) return "";
        try { return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(text[Prefix.Length..]), encrypt: false)); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or DllNotFoundException or EntryPointNotFoundException)
        { return ""; }
    }

    // ------------------------------------------------------------ DPAPI (crypt32.dll)

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private const int Headless = 0x1;   // CRYPTPROTECT_UI_FORBIDDEN

    private static byte[] Transform(byte[] data, bool encrypt)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        var entropy = new Blob { Size = Entropy.Length, Data = Marshal.AllocHGlobal(Entropy.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            Marshal.Copy(Entropy, 0, entropy.Data, Entropy.Length);
            var ok = encrypt
                ? CryptProtectData(ref input, "stackdev", ref entropy, IntPtr.Zero, IntPtr.Zero, Headless, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, Headless, out output);
            if (!ok) throw new InvalidOperationException("DPAPI falló: " + Marshal.GetLastWin32Error());
            try
            {
                var res = new byte[output.Size];
                Marshal.Copy(output.Data, res, 0, output.Size);
                return res;
            }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            Marshal.FreeHGlobal(entropy.Data);
        }
    }
}
