using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackDev.Core.Security;

namespace StackDev.Core.Credentials;

/// <summary>A test credential (app user, database, admin panel...).</summary>
public sealed class Credential
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

/// <summary>
/// A project's credentials, in <c>credentials.json</c> inside the personal configuration folder
/// (outside the repository, so they never reach a commit). The file is encrypted with Windows DPAPI (only your user, on this computer).
/// A legacy plain-text file is read and saved again encrypted.
/// </summary>
public sealed class CredentialsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath { get; }

    public CredentialsStore(string folder) { FilePath = Path.Combine(folder, "credenciales.json"); }

    public List<Credential> Read()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var text = File.ReadAllText(FilePath, Encoding.UTF8).Trim();
            var flat = Protection.IsEncrypted(text) ? Protection.Decrypt(text) : text;
            if (flat.Length == 0) return new();   // encrypted by another user/computer: it cannot be read
            flat = Config.LegacyKeys.Upgrade(flat, Config.LegacyKeys.Credential, out var upgraded);
            var list = JsonSerializer.Deserialize<List<Credential>>(flat, Options) ?? new();
            if ((upgraded || !Protection.IsEncrypted(text)) && Protection.Available && list.Count > 0) Save(list);   // migrates the legacy plain text
            return list;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // a damaged file must not be lost when saving the next one
            try { File.Copy(FilePath, FilePath + ".dañado", true); } catch (IOException) { }
            return new();
        }
    }

    /// <summary>Adds credentials, replacing the ones that already have the same name (a renewed password, for example).</summary>
    public void Upsert(IEnumerable<Credential> items)
    {
        var list = Read();
        foreach (var c in items)
        {
            var i = list.FindIndex(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) list[i] = c; else list.Add(c);
        }
        Save(list);
    }

    public void Save(IEnumerable<Credential> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, Protection.Encrypt(JsonSerializer.Serialize(list.ToList(), Options)), new UTF8Encoding(false));
    }
}
