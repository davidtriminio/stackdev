using System.Text;
using System.Text.Json;
using StackDev.Core.Security;

namespace StackDev.Core.Servers;

/// <summary>La lista global de servidores, en %APPDATA%\stackdev\servidores.json y cifrada con DPAPI.</summary>
public sealed class ServerStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;

    public ServerStore(string dirState)
    {
        _dir = dirState;
        _path = Path.Combine(dirState, "servidores.json");
    }

    private readonly string _dir;

    public List<Server> Read()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var text = Protection.Decrypt(File.ReadAllText(_path, Encoding.UTF8).Trim());
            if (string.IsNullOrWhiteSpace(text)) return new();
            return JsonSerializer.Deserialize<List<Server>>(text, Options) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(IEnumerable<Server> list)
    {
        Directory.CreateDirectory(_dir);
        var json = JsonSerializer.Serialize(list.ToList(), Options);
        File.WriteAllText(_path, Protection.Encrypt(json), new UTF8Encoding(false));
    }

    public Server? Search(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Read().FirstOrDefault(s => s.Id == id);
}
