using System.Security.Cryptography;

namespace StackDev.Core.Deploy;

/// <summary>
/// Settings an API refused to start without (for example <c>Identity__Jwt__SigningKey</c>). The server script reads them from the
/// service log; here they are checked and, for secret-like names, filled with strong random values.
/// </summary>
public static class MissingSettings
{
    /// <summary>Names from the <c>missing</c> value of the script, keeping only valid environment variable names.</summary>
    public static IReadOnlyList<string> Parse(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(EnvFile.IsValidKey).Distinct().ToList();

    /// <summary>True for names that hold a key or password, where a random value is a good answer.</summary>
    public static bool CanGenerate(string name) => SecretMask.IsSecretName(name) || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("SigningKey", StringComparison.OrdinalIgnoreCase);

    public static string RandomSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Sets every name that is missing or empty in <paramref name="env"/> and can be generated. Returns what was set, and what still needs a human value.</summary>
    public static (List<string> Generated, List<string> Manual) Fill(EnvFile env, IEnumerable<string> names)
    {
        var generated = new List<string>();
        var manual = new List<string>();
        foreach (var n in names)
        {
            if (!string.IsNullOrEmpty(env.Get(n))) continue;
            if (CanGenerate(n)) { env.Set(n, RandomSecret()); generated.Add(n); }
            else manual.Add(n);
        }
        return (generated, manual);
    }

    /// <summary>Plain-words steps for the settings only the user can provide, with a hint about where each value comes from.</summary>
    public static string Guide(string service, IEnumerable<string> manual)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Estos valores solo los puedes dar tú:");
        foreach (var n in manual) sb.AppendLine("  • " + n + " — " + Hint(n));
        sb.AppendLine();
        sb.AppendLine("Qué hacer:");
        sb.AppendLine($"  1. En esta pantalla abre «Variables (.env)» del servidor, en la API «{service}».");
        sb.AppendLine("  2. Pulsa «Agregar» y escribe el nombre exactamente como aparece arriba.");
        sb.AppendLine("  3. Pega el valor y guarda: la app reinicia la API y, si no arranca, deja el archivo como estaba.");
        sb.AppendLine("  4. Si la subida se había deshecho, vuelve a pulsar «Subir al servidor».");
        return sb.ToString().TrimEnd();
    }

    private static string Hint(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("connectionstring") || n.Contains("database")) return "la cadena de conexión de tu base de datos";
        if (n.Contains("smtp") || n.Contains("mail")) return "dato de tu proveedor de correo (host, usuario o clave)";
        if (n.Contains("stripe") || n.Contains("payment") || n.Contains("paypal")) return "se copia del panel de tu proveedor de pagos";
        if (n.Contains("apikey") || n.Contains("api_key") || n.Contains("token")) return "se crea en el panel del servicio que usa tu API";
        if (n.Contains("origin") || n.Contains("cors") || n.Contains("url")) return "la dirección completa (https://…) de tu app web";
        return "el valor que pide tu proyecto (búscalo en su README o en appsettings.json)";
    }
}
