using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackDev.Core.Security;

namespace StackDev.Core.Config;

/// <summary>
/// Reading and writing dev-menu.json: 2-space indentation, "\n" line breaks, without escaping
/// single quotes or accents, and tolerant of comments and trailing commas when reading.
/// </summary>
public static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads the configuration; secrets saved as <c>dpapi:…</c> are decrypted (in memory they are always in the clear).</summary>
    public static ProjectConfig Read(string json)
    {
        json = LegacyKeys.Upgrade(json, LegacyKeys.Project, out _);
        var cfg = JsonSerializer.Deserialize<ProjectConfig>(json, Options) ?? new ProjectConfig();
        Transform(cfg, Protection.Decrypt, onlySecrets: false);
        return cfg;
    }

    /// <summary>Plain JSON (working copies, detection screens). To save to disk use <see cref="WriteProtected"/>.</summary>
    public static string Write(ProjectConfig config) =>
        JsonSerializer.Serialize(config, Options) + "\n";

    /// <summary>JSON to save to disk: the Postgres password and the API variables that look like secrets are encrypted with DPAPI.</summary>
    public static string WriteProtected(ProjectConfig config)
    {
        if (!Protection.Available) return Write(config);
        var copy = JsonSerializer.Deserialize<ProjectConfig>(JsonSerializer.Serialize(config, Options), Options) ?? new ProjectConfig();
        Transform(copy, Protection.Encrypt, onlySecrets: true);
        return Write(copy);
    }

    private static void Transform(ProjectConfig cfg, Func<string?, string> f, bool onlySecrets)
    {
        if (cfg.Postgres?.Password is { Length: > 0 } pw) cfg.Postgres.Password = f(pw);
        if (cfg.Api?.Env is { } env)
            foreach (var k in env.Keys.ToList())
                if (!onlySecrets || Protection.LooksSecret(k)) env[k] = f(env[k]);
    }
}
