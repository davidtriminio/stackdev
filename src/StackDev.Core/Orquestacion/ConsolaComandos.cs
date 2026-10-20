using StackDev.Core.Config;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Orquestacion;

public sealed record ItemComando(string Carpeta, string Comando, bool Tarea, string Titulo);

/// <summary>
/// Una consola para ejecutar cualquier comando en una carpeta del proyecto: tareas de la configuración,
/// historial de los últimos 10 por proyecto, cambio de carpeta (<c>cd</c> y <c>@api</c> <c>@web</c> <c>@movil</c> <c>@raiz</c>),
/// <c>v &lt;comando&gt;</c> en otra ventana y <c>t</c> para abrir una terminal.
/// </summary>
public sealed class ConsolaComandos
{
    private static readonly Dictionary<string, string> Alias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@api"] = "api", ["@web"] = "web", ["@movil"] = "movil", ["@mobile"] = "movil", ["@raiz"] = "raiz",
    };

    private readonly Sesion _s;

    public ConsolaComandos(Sesion s)
    {
        _s = s;
        Dir = s.Raiz;
    }

    public string Dir { get; private set; }

    public string Prompt => $"[{_s.Rel(Dir)}]>";

    /// <summary>Tareas del proyecto y comandos recientes, numerados en ese orden.</summary>
    public List<ItemComando> Items()
    {
        var items = new List<ItemComando>();
        foreach (var t in _s.Ctx.Tareas)
            items.Add(new ItemComando(t.Carpeta ?? ".", t.Comando ?? "", true, $"{t.Nombre,-34} [{(string.IsNullOrEmpty(t.Carpeta) ? "." : t.Carpeta)}]"));
        foreach (var r in _s.Estado.LeerRecientes(_s.Clave))
            items.Add(new ItemComando(r.Carpeta, r.Comando, false, $"[{r.Carpeta}] {r.Comando}"));
        return items;
    }

    public string Ayuda()
    {
        var alias = string.Join(" ", Alias.Where(kv => kv.Value == "raiz" || _s.Ctx.Tiene(Comp(kv.Value))).Select(kv => kv.Key).OrderBy(x => x));
        var extra = _s.Ctx.EnvParaCarpeta(Dir) is not null ? "\n  (en esta carpeta se aplican las variables de la API, p. ej. la cadena de conexión)" : "";
        return $"  Escribe un comando | número = ejecutar | v <comando> = en otra ventana\n  cd <carpeta> o {alias} | t = abrir terminal | Enter = volver{extra}";
    }

    private static Componente Comp(string n) => n switch { "api" => Componente.Api, "web" => Componente.Web, _ => Componente.Movil };

    /// <summary>Procesa una línea. Devuelve false cuando hay que salir de la consola (línea vacía).</summary>
    public bool Procesar(string? entrada)
    {
        entrada = entrada?.Trim();
        if (string.IsNullOrEmpty(entrada)) return false;

        if (entrada == "t") { _s.Ejecutor.AbrirTerminal(Dir); return true; }

        if (Alias.TryGetValue(entrada, out var comp))
        {
            if (comp == "raiz") Dir = _s.Raiz;
            else if (_s.Ctx.Tiene(Comp(comp))) Dir = _s.Ctx.Carpeta(Comp(comp));
            else _s.Ui.Escribir($"Este proyecto no tiene {comp}.", Nivel.Aviso);
            return true;
        }
        if (entrada.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
        {
            var dest = entrada[3..].Trim().Trim('"');
            var nuevo = Path.IsPathRooted(dest) ? dest : Path.Combine(Dir, dest);
            if (Directory.Exists(nuevo)) Dir = Path.GetFullPath(nuevo);
            else _s.Ui.Escribir($"No existe: {nuevo}", Nivel.Error);
            return true;
        }

        var enVentana = false;
        if (entrada.StartsWith("v ", StringComparison.OrdinalIgnoreCase)) { enVentana = true; entrada = entrada[2..].Trim(); }

        var carpetaCmd = Dir;
        var comando = entrada;
        var rel = _s.Rel(Dir);
        if (int.TryParse(entrada, out var n))
        {
            var items = Items();
            if (n < 1 || n > items.Count) { _s.Ui.Escribir("Número fuera de rango.", Nivel.Error); return true; }
            var it = items[n - 1];
            carpetaCmd = Rutas.Resolver(_s.Raiz, it.Carpeta);
            var expandido = Preguntas.Expandir(it.Comando, t => _s.Ui.Preguntar(t));
            if (expandido is null) { _s.Ui.Escribir("Cancelado.", Nivel.Tenue); return true; }
            comando = expandido;
            if (!it.Tarea) _s.Estado.AgregarReciente(_s.Clave, it.Carpeta, it.Comando);
        }
        else _s.Estado.AgregarReciente(_s.Clave, rel, comando);

        if (enVentana)
        {
            if (_s.Ejecutor.EnVentana("cmd", comando, carpetaCmd, comando))
                _s.Ui.Escribir($"Corriendo en otra ventana: {comando}", Nivel.Tenue);
        }
        else _s.Ejecutor.Aqui(comando, carpetaCmd);
        return true;
    }

    /// <summary>Ejecuta una tarea de la configuración por nombre (o parte del nombre). Para <c>stackdev tarea "..."</c>.</summary>
    public int EjecutarTarea(string nombre)
    {
        var t = _s.Ctx.Tareas.FirstOrDefault(x => string.Equals(x.Nombre, nombre, StringComparison.OrdinalIgnoreCase))
             ?? _s.Ctx.Tareas.FirstOrDefault(x => x.Nombre?.Contains(nombre, StringComparison.OrdinalIgnoreCase) == true);
        if (t is null) { _s.Ui.Escribir($"No hay una tarea llamada '{nombre}'.", Nivel.Error); return 1; }
        var cmd = Preguntas.Expandir(t.Comando ?? "", p => _s.Ui.Preguntar(p));
        if (cmd is null) { _s.Ui.Escribir("Cancelado.", Nivel.Tenue); return 1; }
        return _s.Ejecutor.Aqui(cmd, Rutas.Resolver(_s.Raiz, t.Carpeta));
    }
}
