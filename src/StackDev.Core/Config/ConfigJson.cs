using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackDev.Core.Security;

namespace StackDev.Core.Config;

/// <summary>
/// Lectura y escritura de dev-menu.json: sangría de 2 espacios, saltos de línea "\n", sin escapar
/// comillas simples ni tildes, y tolerante a comentarios y comas finales al leer.
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

    /// <summary>Lee la configuración; los secretos guardados como <c>dpapi:…</c> se descifran (en memoria siempre están en claro).</summary>
    public static ProjectConfig Read(string json)
    {
        var cfg = JsonSerializer.Deserialize<ProjectConfig>(json, Options) ?? new ProjectConfig();
        Transform(cfg, Protection.Decrypt, onlySecrets: false);
        return cfg;
    }

    /// <summary>JSON en claro (copias de trabajo, pantallas de detección). Para guardar en disco se usa <see cref="WriteProtected"/>.</summary>
    public static string Write(ProjectConfig config) =>
        JsonSerializer.Serialize(config, Options) + "\n";

    /// <summary>JSON para guardar en disco: la contraseña de Postgres y las variables de la API que parecen secretos van cifradas con DPAPI.</summary>
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
