using System.Text.RegularExpressions;

namespace StackDev.Core.Detection;

/// <summary>
/// Busca el Android SDK. El SDK no está en las variables de entorno globales: se usa solo para los
/// procesos que lanza la herramienta (emulador, adb, flutter).
/// </summary>
public static class AndroidSdkLocator
{
    public static string EmulatorExe(string sdk) =>
        Path.Combine(sdk, "emulator", OperatingSystem.IsWindows() ? "emulator.exe" : "emulator");

    public static string AdbExe(string sdk) =>
        Path.Combine(sdk, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb");

    public static bool IsValid(string? sdk) =>
        !string.IsNullOrWhiteSpace(sdk) && File.Exists(EmulatorExe(sdk));

    /// <summary>El valor de "android-sdk" en el archivo %APPDATA%\.flutter_settings (con \\ convertido a \).</summary>
    public static string? FromFlutterSettings(string textJson)
    {
        var m = Regex.Match(textJson, "\"android-sdk\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : null;
    }

    /// <summary>
    /// Orden: <c>mobile.androidSdk</c> → ANDROID_SDK_ROOT → ANDROID_HOME → .flutter_settings → %LOCALAPPDATA%\Android\Sdk.
    /// Devuelve null si ninguno es válido (entonces se pregunta).
    /// </summary>
    public static string? Search(string? configured, Func<string, string?> variable, string? flutterSettings,
        string? localAppData, Func<string, bool>? isValid = null)
    {
        isValid ??= IsValid;
        var candidates = new List<string?>
        {
            configured,
            variable("ANDROID_SDK_ROOT"),
            variable("ANDROID_HOME"),
            flutterSettings is null ? null : FromFlutterSettings(flutterSettings),
            string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "Android", "Sdk"),
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && isValid(c!));
    }

    /// <summary>Variables que se ponen solo en el entorno de los procesos que lanza la herramienta.</summary>
    public static IReadOnlyDictionary<string, string> ProcessVariables(string sdk) =>
        new Dictionary<string, string> { ["ANDROID_SDK_ROOT"] = sdk, ["ANDROID_HOME"] = sdk };
}
