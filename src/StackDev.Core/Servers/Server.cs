namespace StackDev.Core.Servers;

/// <summary>A server a project can be uploaded to. It is saved encrypted in the personal state (it is not in the repo).</summary>
public sealed class Server
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string User { get; set; } = "opc";
    public int Port { get; set; } = 22;
    /// <summary>Path to the private SSH key.</summary>
    public string Key { get; set; } = "";
    /// <summary>Web server acting as the proxy: "apache" or "nginx".</summary>
    public string Web { get; set; } = "apache";

    public string Destination => $"{User}@{Host}" + (Port is > 0 and not 22 ? $":{Port}" : "");
    public string WebName => string.Equals(Web, "nginx", StringComparison.OrdinalIgnoreCase) ? "Nginx" : "Apache";
}
