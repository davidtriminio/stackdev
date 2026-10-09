namespace StackDev.Core.Ui;

/// <summary>
/// Implementación sobre System.Console, para los comandos sin interfaz (scripts).
/// Sin persona delante contesta con los valores por defecto (o "sí" a todo con <c>--si</c>).
/// </summary>
public sealed class ConsolaSimple : IConsolaUi
{
    private readonly bool _siATodo;

    public ConsolaSimple(bool interactiva, bool siATodo = false)
    {
        Interactiva = interactiva && !siATodo;
        _siATodo = siATodo;
    }

    public bool Interactiva { get; }

    public void Escribir(string texto, Nivel nivel = Nivel.Normal)
    {
        var color = nivel switch
        {
            Nivel.Titulo => ConsoleColor.Cyan,
            Nivel.Info => ConsoleColor.Cyan,
            Nivel.Ok => ConsoleColor.Green,
            Nivel.Aviso => ConsoleColor.Yellow,
            Nivel.Error => ConsoleColor.Red,
            Nivel.Tenue => ConsoleColor.DarkGray,
            _ => (ConsoleColor?)null,
        };
        if (color is null || Console.IsOutputRedirected) { Console.WriteLine(texto); return; }
        var antes = Console.ForegroundColor;
        Console.ForegroundColor = color.Value;
        Console.WriteLine(texto);
        Console.ForegroundColor = antes;
    }

    public void EscribirInline(string texto) => Console.Write(texto);

    public bool Confirmar(string pregunta, bool porDefecto = true)
    {
        if (_siATodo) return true;
        if (!Interactiva) return porDefecto;
        Console.Write($"{pregunta} ({(porDefecto ? "S/n" : "s/N")}) ");
        var r = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (r.Length == 0) return porDefecto;
        return r is "s" or "si" or "sí" or "y" or "yes";
    }

    public string? Preguntar(string pregunta, string? porDefecto = null)
    {
        if (!Interactiva) return porDefecto;
        Console.Write(porDefecto is null ? $"{pregunta}: " : $"{pregunta} (Enter = {porDefecto}): ");
        var r = (Console.ReadLine() ?? "").Trim();
        return r.Length == 0 ? porDefecto : r;
    }

    public int Elegir(string titulo, IReadOnlyList<string> opciones, int porDefecto = -1)
    {
        if (!Interactiva) return porDefecto;
        Console.WriteLine(titulo);
        for (var i = 0; i < opciones.Count; i++) Console.WriteLine($"  [{i + 1}] {opciones[i]}");
        while (true)
        {
            Console.Write(porDefecto >= 0 ? $"Opción (Enter = {porDefecto + 1}, c = cancelar): " : "Opción (c = cancelar): ");
            var r = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (r.Length == 0 && porDefecto >= 0) return porDefecto;
            if (r == "c") return -1;
            if (int.TryParse(r, out var n) && n >= 1 && n <= opciones.Count) return n - 1;
            Console.WriteLine("Opción no válida.");
        }
    }
}
