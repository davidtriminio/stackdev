namespace StackDev.Core.Servidores;

/// <summary>Un servidor al que se puede subir un proyecto. Se guarda cifrado en el estado personal (no va en el repo).</summary>
public sealed class Servidor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Nombre { get; set; } = "";
    public string Host { get; set; } = "";
    public string Usuario { get; set; } = "opc";
    public int Puerto { get; set; } = 22;
    /// <summary>Ruta de la clave SSH privada.</summary>
    public string Clave { get; set; } = "";
    /// <summary>Servidor web que hace de proxy: «apache» o «nginx».</summary>
    public string Web { get; set; } = "apache";

    public string Destino => $"{Usuario}@{Host}" + (Puerto is > 0 and not 22 ? $":{Puerto}" : "");
    public string WebNombre => string.Equals(Web, "nginx", StringComparison.OrdinalIgnoreCase) ? "Nginx" : "Apache";
}
