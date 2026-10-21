using System.Text;

namespace StackDev.Core.Util;

/// <summary>Armado y escape de argumentos para procesos externos y para scripts de PowerShell.</summary>
public static class Args
{
    /// <summary>
    /// Escapa un argumento con las reglas de CommandLineToArgvW (las que usan casi todos los programas de Windows):
    /// se ponen comillas si tiene espacios o comillas, y las barras invertidas antes de una comilla se duplican.
    /// </summary>
    public static string Quote(string argument)
    {
        if (argument.Length == 0) return "\"\"";
        var needs = false;
        foreach (var c in argument)
        {
            if (char.IsWhiteSpace(c) || c == '"') { needs = true; break; }
        }
        if (!needs) return argument;

        var sb = new StringBuilder("\"");
        var bars = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { bars++; continue; }
            if (c == '"')
            {
                sb.Append('\\', bars * 2 + 1).Append('"');
                bars = 0;
                continue;
            }
            sb.Append('\\', bars).Append(c);
            bars = 0;
        }
        sb.Append('\\', bars * 2).Append('"');
        return sb.ToString();
    }

    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    /// <summary>Literal de PowerShell entre comillas simples: 'it''s'.</summary>
    public static string PsLiteral(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>
    /// Parte una línea de comando en argumentos respetando comillas dobles
    /// (para convertir "dotnet run --project src/Api" en ejecutable + argumentos).
    /// </summary>
    public static List<string> Split(string line)
    {
        var res = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var has = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\')
            {
                var j = i;
                while (j < line.Length && line[j] == '\\') j++;
                var n = j - i;
                if (j < line.Length && line[j] == '"')
                {
                    current.Append('\\', n / 2);
                    if (n % 2 == 1) { current.Append('"'); i = j; } else { i = j - 1; }
                }
                else { current.Append('\\', n); i = j - 1; }
                has = true;
            }
            else if (c == '"') { inQuotes = !inQuotes; has = true; }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (has) { res.Add(current.ToString()); current.Clear(); has = false; }
            }
            else { current.Append(c); has = true; }
        }
        if (has) res.Add(current.ToString());
        return res;
    }
}
