using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackDev.Core.Seguridad;

namespace StackDev.Core.Credenciales;

/// <summary>Una credencial de prueba (usuario de la app, base de datos, panel de administración...).</summary>
public sealed class Credencial
{
    [JsonPropertyName("nombre")] public string Nombre { get; set; } = "";
    [JsonPropertyName("usuario")] public string Usuario { get; set; } = "";
    [JsonPropertyName("clave")] public string Clave { get; set; } = "";
    [JsonPropertyName("nota")] public string Nota { get; set; } = "";
}

/// <summary>
/// Las credenciales de un proyecto, en <c>credenciales.json</c> dentro de la carpeta de configuración personal
/// (fuera del repositorio, así no llegan a un commit). El archivo va cifrado con DPAPI de Windows (solo tu usuario, en este equipo).
/// Un archivo heredado en texto plano se lee y se vuelve a guardar cifrado.
/// </summary>
public sealed class CredencialesStore
{
    private static readonly JsonSerializerOptions Opciones = new() { WriteIndented = true };

    public string Ruta { get; }

    public CredencialesStore(string carpeta) { Ruta = Path.Combine(carpeta, "credenciales.json"); }

    public List<Credencial> Leer()
    {
        if (!File.Exists(Ruta)) return new();
        try
        {
            var texto = File.ReadAllText(Ruta, Encoding.UTF8).Trim();
            var plano = Proteccion.EstaCifrado(texto) ? Proteccion.Descifrar(texto) : texto;
            if (plano.Length == 0) return new();   // cifrado por otro usuario/equipo: no se puede leer
            var lista = JsonSerializer.Deserialize<List<Credencial>>(plano, Opciones) ?? new();
            if (!Proteccion.EstaCifrado(texto) && Proteccion.Disponible && lista.Count > 0) Guardar(lista);   // migra el texto plano heredado
            return lista;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // un archivo dañado no debe perderse al guardar lo siguiente
            try { File.Copy(Ruta, Ruta + ".dañado", true); } catch (IOException) { }
            return new();
        }
    }

    public void Guardar(IEnumerable<Credencial> lista)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Ruta)!);
        File.WriteAllText(Ruta, Proteccion.Cifrar(JsonSerializer.Serialize(lista.ToList(), Opciones)), new UTF8Encoding(false));
    }
}
