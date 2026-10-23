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
}
