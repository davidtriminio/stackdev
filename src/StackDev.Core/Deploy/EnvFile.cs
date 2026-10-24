using System.Text;
using System.Text.RegularExpressions;

namespace StackDev.Core.Deploy;

/// <summary>One line of an environment file: a variable, or a comment/blank line kept as it was.</summary>
public sealed class EnvLine
{
    public string? Key { get; init; }
    public string Value { get; set; } = "";
    /// <summary>Raw text for lines that are not variables (comments, blank lines, anything unparseable).</summary>
    public string? Raw { get; init; }
    public bool IsVariable => Key is not null;
}

/// <summary>
/// The systemd EnvironmentFile format used by the API service: <c>KEY=value</c>, optional double or single quotes,
/// <c>#</c> comments. Comments and order are preserved when it is written back.
/// </summary>
public sealed class EnvFile
{
    private static readonly Regex KeyPattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public List<EnvLine> Lines { get; } = new();

    public IEnumerable<EnvLine> Variables => Lines.Where(l => l.IsVariable);

    public static bool IsValidKey(string key) => KeyPattern.IsMatch(key);

    public static EnvFile Parse(string text)
    {
        var f = new EnvFile();
        foreach (var raw in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0 || t[0] == '#' || t[0] == ';') { f.Lines.Add(new EnvLine { Raw = raw }); continue; }
            var eq = t.IndexOf('=');
            if (eq <= 0 || !IsValidKey(t[..eq].Trim())) { f.Lines.Add(new EnvLine { Raw = raw }); continue; }
            f.Lines.Add(new EnvLine { Key = t[..eq].Trim(), Value = Unquote(t[(eq + 1)..].Trim()) });
        }
        // the split of a text that ends with a newline leaves one empty trailing line
        if (f.Lines.Count > 0 && f.Lines[^1].Raw == "") f.Lines.RemoveAt(f.Lines.Count - 1);
        return f;
    }

    private static string Unquote(string v)
    {
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
        {
            var sb = new StringBuilder();
            for (var i = 1; i < v.Length - 1; i++)
            {
                if (v[i] == '\\' && i + 1 < v.Length - 1)
                {
                    i++;
                    sb.Append(v[i] switch { 'n' => '\n', 't' => '\t', _ => v[i] });
                }
                else sb.Append(v[i]);
            }
            return sb.ToString();
        }
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') return v[1..^1];
        return v;
    }

    private static string Quote(string v)
    {
        if (v.Length > 0 && !Regex.IsMatch(v, @"[\s#;""'\\$`]")) return v;
        if (v.Length == 0) return "";
        return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    public string? Get(string key) => Variables.FirstOrDefault(l => l.Key == key)?.Value;

    public bool Has(string key) => Variables.Any(l => l.Key == key);

    /// <summary>Changes a variable, or adds it at the end. Throws for names or values that would corrupt the file.</summary>
    public void Set(string key, string value)
    {
        if (!IsValidKey(key)) throw new ArgumentException("Nombre de variable no válido: " + key);
        if (value.Contains('\n') || value.Contains('\r')) throw new ArgumentException("El valor de " + key + " no puede tener saltos de línea.");
        var existing = Variables.FirstOrDefault(l => l.Key == key);
        if (existing is not null) existing.Value = value;
        else Lines.Add(new EnvLine { Key = key, Value = value });
    }

    public bool Remove(string key) => Lines.RemoveAll(l => l.Key == key) > 0;

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var l in Lines)
            sb.Append(l.IsVariable ? $"{l.Key}={Quote(l.Value)}" : l.Raw).Append('\n');
        return sb.ToString();
    }

    /// <summary>The value as it may be shown: secrets are hidden.</summary>
    public static string Display(string key, string value) =>
        value.Length == 0 ? "" : SecretMask.IsSecretName(key) ? SecretMask.Hidden : SecretMask.Mask(value);

    /// <summary>Every secret value in the file, to hide them from logs.</summary>
    public IEnumerable<string> SecretValues() =>
        Variables.Where(v => SecretMask.IsSecretName(v.Key!) && v.Value.Length >= 4).Select(v => v.Value);
}
