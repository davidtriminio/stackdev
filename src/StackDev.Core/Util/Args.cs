using System.Text;

namespace StackDev.Core.Util;

/// <summary>Armado y escape de argumentos para procesos externos y para scripts de PowerShell.</summary>
public static class Args
{
    /// <summary>
    /// Escapa un argumento con las reglas de CommandLineToArgvW (las que usan casi todos los programas de Windows):
    /// se ponen comillas si tiene espacios o comillas, y las barras invertidas antes de una comilla se duplican.
    /// </summary>
    public static string Quote(string argumento)
    {
        if (argumento.Length == 0) return "\"\"";
        var necesita = false;
        foreach (var c in argumento)
        {
            if (char.IsWhiteSpace(c) || c == '"') { necesita = true; break; }
        }
        if (!necesita) return argumento;

        var sb = new StringBuilder("\"");
        var barras = 0;
        foreach (var c in argumento)
        {
            if (c == '\\') { barras++; continue; }
            if (c == '"')
            {
                sb.Append('\\', barras * 2 + 1).Append('"');
                barras = 0;
                continue;
            }
            sb.Append('\\', barras).Append(c);
            barras = 0;
        }
        sb.Append('\\', barras * 2).Append('"');
        return sb.ToString();
    }

    public static string Join(IEnumerable<string> argumentos) => string.Join(' ', argumentos.Select(Quote));

    /// <summary>Literal de PowerShell entre comillas simples: 'it''s'.</summary>
    public static string PsLiteral(string texto) => "'" + texto.Replace("'", "''") + "'";

    /// <summary>
    /// Parte una línea de comando en argumentos respetando comillas dobles
    /// (para convertir "dotnet run --project src/Api" en ejecutable + argumentos).
    /// </summary>
    public static List<string> Dividir(string linea)
    {
        var res = new List<string>();
        var actual = new StringBuilder();
        var enComillas = false;
        var hay = false;
        for (var i = 0; i < linea.Length; i++)
        {
            var c = linea[i];
            if (c == '\\')
            {
                var j = i;
                while (j < linea.Length && linea[j] == '\\') j++;
                var n = j - i;
                if (j < linea.Length && linea[j] == '"')
                {
                    actual.Append('\\', n / 2);
                    if (n % 2 == 1) { actual.Append('"'); i = j; } else { i = j - 1; }
                }
                else { actual.Append('\\', n); i = j - 1; }
                hay = true;
            }
            else if (c == '"') { enComillas = !enComillas; hay = true; }
            else if (char.IsWhiteSpace(c) && !enComillas)
            {
                if (hay) { res.Add(actual.ToString()); actual.Clear(); hay = false; }
            }
            else { actual.Append(c); hay = true; }
        }
        if (hay) res.Add(actual.ToString());
        return res;
    }
}
