using System.Text.RegularExpressions;

namespace StackDev.Core.Detection;

/// <summary>
/// Looks for the Android SDK. The SDK is not in the global environment variables: it is used only for the
/// processes the tool launches (emulator, adb, flutter).
/// </summary>
public static class AndroidSdkLocator
{
    public static string EmulatorExe(string sdk) =>
        Path.Combine(sdk, "emulator", OperatingSystem.IsWindows() ? "emulator.exe" : "emulator");

    public static string AdbExe(string sdk) =>
        Path.Combine(sdk, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb");

    public static bool IsValid(string? sdk) =>
        !string.IsNullOrWhiteSpace(sdk) && File.Exists(EmulatorExe(sdk));

    /// <summary>The value of "android-sdk" in the %APPDATA%\.flutter_settings file (with \\ converted to \).</summary>
    public static string? FromFlutterSettings(string textJson)
    {
        var m = Regex.Match(textJson, "\"android-sdk\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : null;
    }

    /// <summary>
    /// Orden: <c>mobile.androidSdk</c> → ANDROID_SDK_ROOT → ANDROID_HOME → .flutter_settings → %LOCALAPPDATA%\Android\Sdk.
    /// Returns null if none is valid (then the user is asked).
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

    /// <summary>Variables set only in the environment of the processes the tool launches.</summary>
    public static IReadOnlyDictionary<string, string> ProcessVariables(string sdk) =>
        new Dictionary<string, string> { ["ANDROID_SDK_ROOT"] = sdk, ["ANDROID_HOME"] = sdk };
}
