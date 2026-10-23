using System.Text.Json;
using System.Text.Json.Nodes;

namespace StackDev.Core.Config;

/// <summary>
/// Reads files written before the identifiers moved to English. Old Spanish JSON keys are renamed to the current ones
/// before deserializing, so existing servers, credentials and dev-menu.json files keep their data.
/// </summary>
public static class LegacyKeys
{
    /// <summary>dev-menu.json: old key to current key (the names are unique across every section).</summary>
    public static readonly IReadOnlyDictionary<string, string> Project = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["nombre"] = "name", ["navegador"] = "browser", ["movil"] = "mobile", ["levantarTodo"] = "startAll",
        ["tareas"] = "tasks", ["despliegue"] = "deployment", ["servidor"] = "server", ["servicio"] = "service",
        ["dominio"] = "domain", ["contenedor"] = "container", ["imagen"] = "image", ["puerto"] = "port",
        ["usuario"] = "user", ["baseDatos"] = "database", ["volumen"] = "volume", ["carpeta"] = "folder",
        ["abrir"] = "open", ["iniciar"] = "start", ["instalar"] = "install", ["analizar"] = "parse",
        ["formato"] = "format", ["generar"] = "generate", ["comando"] = "command",
    };

    /// <summary>credenciales.json.</summary>
    public static readonly IReadOnlyDictionary<string, string> Credential = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["nombre"] = "name", ["usuario"] = "user", ["clave"] = "key", ["nota"] = "note",
    };

    /// <summary>servidores.json.</summary>
    public static readonly IReadOnlyDictionary<string, string> Server = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["nombre"] = "name", ["usuario"] = "user", ["puerto"] = "port", ["clave"] = "key",
    };

    /// <summary>
    /// Renames old keys everywhere in <paramref name="json"/>. A key is only renamed when the current name is not already
    /// present in the same object. <paramref name="changed"/> tells whether anything was renamed.
    /// Text that is not valid JSON is returned untouched, so the caller reports the usual parse error.
    /// </summary>
    public static string Upgrade(string json, IReadOnlyDictionary<string, string> map, out bool changed)
    {
        changed = false;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException) { return json; }
        if (root is null) return json;
        changed = Walk(root, map);
        return changed ? root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : json;
    }

    private static bool Walk(JsonNode node, IReadOnlyDictionary<string, string> map)
    {
        var changed = false;
        switch (node)
        {
            case JsonArray arr:
                foreach (var item in arr) if (item is not null && Walk(item, map)) changed = true;
                break;
            case JsonObject obj:
                foreach (var (oldName, newName) in obj.Select(p => p.Key).Where(map.ContainsKey).Select(k => (k, map[k])).ToList())
                {
                    if (string.Equals(oldName, newName, StringComparison.Ordinal)) continue;
                    if (obj.Any(p => string.Equals(p.Key, newName, StringComparison.OrdinalIgnoreCase)) && !string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) continue;
                    var value = obj[oldName];
                    obj.Remove(oldName);
                    obj[newName] = value;
                    changed = true;
                }
                // values of "env" are user-chosen variable names, never schema keys
                foreach (var (key, value) in obj.ToList())
                    if (value is not null && !string.Equals(key, "env", StringComparison.OrdinalIgnoreCase) && Walk(value, map)) changed = true;
                break;
        }
        return changed;
    }
}
