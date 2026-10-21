using System.Text;
using System.Text.Json;
using StackDev.Core.Seguridad;

namespace StackDev.Core.Servidores;

/// <summary>La lista global de servidores, en %APPDATA%\stackdev\servidores.json y cifrada con DPAPI.</summary>
public sealed class ServidoresStore
{
    private static readonly JsonSerializerOptions Opciones = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _ruta;

    public ServidoresStore(string dirEstado)
    {
        _dir = dirEstado;
        _ruta = Path.Combine(dirEstado, "servidores.json");
    }

    private readonly string _dir;

    public List<Servidor> Leer()
    {
        if (!File.Exists(_ruta)) return new();
        try
        {
            var texto = Proteccion.Descifrar(File.ReadAllText(_ruta, Encoding.UTF8).Trim());
            if (string.IsNullOrWhiteSpace(texto)) return new();
            return JsonSerializer.Deserialize<List<Servidor>>(texto, Opciones) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Guardar(IEnumerable<Servidor> lista)
    {
        Directory.CreateDirectory(_dir);
        var json = JsonSerializer.Serialize(lista.ToList(), Opciones);
        File.WriteAllText(_ruta, Proteccion.Cifrar(json), new UTF8Encoding(false));
    }

    public Servidor? Buscar(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Leer().FirstOrDefault(s => s.Id == id);
}
