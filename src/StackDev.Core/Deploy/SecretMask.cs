using System.Text.RegularExpressions;

namespace StackDev.Core.Deploy;

/// <summary>Hides passwords, tokens, keys and connection-string secrets so they never reach a log, the screen or a file.</summary>
public static class SecretMask
{
    public const string Hidden = "******";

    private const string SecretName = @"[A-Za-z0-9_.:\-]*(?:password|passwd|pwd|secret|token|apikey|api_key|accesskey|access_key|private|credential|__key|_key|jwtkey)[A-Za-z0-9_.:\-]*";

    private static readonly Regex PrivateKeyBlock = new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Assignment = new(@"(?<name>\b" + SecretName + @")(?<sep>\s*[=:]\s*)(?<value>""[^""]*""|'[^']*'|[^;\s,""']+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Bearer = new(@"(?<pre>\b(?:Bearer|Basic)\s+)[A-Za-z0-9\-._~+/=]{8,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UrlUserInfo = new(@"(?<pre>[a-z][a-z0-9+.\-]*://[^/\s:@]+:)[^/\s@]+(?=@)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns the text with every recognizable secret replaced. <paramref name="known"/> are literal values to hide wherever they appear.</summary>
    public static string Mask(string? text, IEnumerable<string>? known = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var t = PrivateKeyBlock.Replace(text, "[private key hidden]");
        t = UrlUserInfo.Replace(t, "${pre}" + Hidden);
        t = Bearer.Replace(t, "${pre}" + Hidden);
        t = Assignment.Replace(t, m =>
        {
            var v = m.Groups["value"].Value;
            var quote = v.Length >= 2 && (v[0] == '"' || v[0] == '\'') ? v[0].ToString() : "";
            return m.Groups["name"].Value + m.Groups["sep"].Value + quote + Hidden + quote;
        });
        if (known is not null)
            foreach (var k in known.Where(k => !string.IsNullOrEmpty(k) && k.Length >= 4).OrderByDescending(k => k.Length))
                t = t.Replace(k, Hidden, StringComparison.Ordinal);
        return t;
    }

    private static readonly Regex SecretNameTest = new(
        @"password|passwd|pwd|secret|token|api_?key|access_?key|private|credential|jwtkey|(^|_)key$|__key|^connectionstrings",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when the variable name suggests it holds a secret.</summary>
    public static bool IsSecretName(string name) => SecretNameTest.IsMatch(name);
}
