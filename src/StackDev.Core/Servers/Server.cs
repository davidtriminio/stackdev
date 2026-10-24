namespace StackDev.Core.Servers;

/// <summary>Un servidor al que se puede subir un proyecto. Se guarda cifrado en el estado personal (no va en el repo).</summary>
public sealed class Server
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string User { get; set; } = "opc";
    public int Port { get; set; } = 22;
    /// <summary>Ruta de la clave SSH privada.</summary>
    public string Key { get; set; } = "";
    /// <summary>Servidor web que hace de proxy: «apache» o «nginx».</summary>
    public string Web { get; set; } = "apache";

    public string Destination => $"{User}@{Host}" + (Port is > 0 and not 22 ? $":{Port}" : "");
    public string WebName => string.Equals(Web, "nginx", StringComparison.OrdinalIgnoreCase) ? "Nginx" : "Apache";
}
