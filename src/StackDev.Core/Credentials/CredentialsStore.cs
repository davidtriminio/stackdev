using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackDev.Core.Security;

namespace StackDev.Core.Credentials;

/// <summary>Una credencial de prueba (usuario de la app, base de datos, panel de administración...).</summary>
public sealed class Credential
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

/// <summary>
/// Las credenciales de un proyecto, en <c>credenciales.json</c> dentro de la carpeta de configuración personal
/// (fuera del repositorio, así no llegan a un commit). El archivo va cifrado con DPAPI de Windows (solo tu usuario, en este equipo).
/// Un archivo heredado en texto plano se lee y se vuelve a guardar cifrado.
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
            if (flat.Length == 0) return new();   // cifrado por otro usuario/equipo: no se puede leer
            var list = JsonSerializer.Deserialize<List<Credential>>(flat, Options) ?? new();
            if (!Protection.IsEncrypted(text) && Protection.Available && list.Count > 0) Save(list);   // migra el texto plano heredado
            return list;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // un archivo dañado no debe perderse al guardar lo siguiente
            try { File.Copy(FilePath, FilePath + ".dañado", true); } catch (IOException) { }
            return new();
        }
    }

    public void Save(IEnumerable<Credential> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, Protection.Encrypt(JsonSerializer.Serialize(list.ToList(), Options)), new UTF8Encoding(false));
    }
}
