using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackDev.Core.Seguridad;

namespace StackDev.Core.Config;

/// <summary>
/// Lectura y escritura de dev-menu.json: sangría de 2 espacios, saltos de línea "\n", sin escapar
/// comillas simples ni tildes, y tolerante a comentarios y comas finales al leer.
/// </summary>
public static class ConfigJson
{
    public static readonly JsonSerializerOptions Opciones = new()
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
    public static ProyectoConfig Leer(string json)
    {
        var cfg = JsonSerializer.Deserialize<ProyectoConfig>(json, Opciones) ?? new ProyectoConfig();
        Transformar(cfg, Proteccion.Descifrar, soloSecretos: false);
        return cfg;
    }

    /// <summary>JSON en claro (copias de trabajo, pantallas de detección). Para guardar en disco se usa <see cref="EscribirProtegido"/>.</summary>
    public static string Escribir(ProyectoConfig config) =>
        JsonSerializer.Serialize(config, Opciones) + "\n";

    /// <summary>JSON para guardar en disco: la contraseña de Postgres y las variables de la API que parecen secretos van cifradas con DPAPI.</summary>
    public static string EscribirProtegido(ProyectoConfig config)
    {
        if (!Proteccion.Disponible) return Escribir(config);
        var copia = JsonSerializer.Deserialize<ProyectoConfig>(JsonSerializer.Serialize(config, Opciones), Opciones) ?? new ProyectoConfig();
        Transformar(copia, Proteccion.Cifrar, soloSecretos: true);
        return Escribir(copia);
    }

    private static void Transformar(ProyectoConfig cfg, Func<string?, string> f, bool soloSecretos)
    {
        if (cfg.Postgres?.Password is { Length: > 0 } pw) cfg.Postgres.Password = f(pw);
        if (cfg.Api?.Env is { } env)
            foreach (var k in env.Keys.ToList())
                if (!soloSecretos || Proteccion.PareceSecreto(k)) env[k] = f(env[k]);
    }
}
