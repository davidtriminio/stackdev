using System.Text.RegularExpressions;

namespace StackDev.Core.Deploy;

/// <summary>What the project itself says about what it needs, so the user does not have to answer questions the code already answers.</summary>
public static class ProjectHints
{
    public sealed record DatabaseHint(DatabaseKind Kind, string Reason);

    /// <summary>
    /// Looks at the packages of the .csproj and at the connection strings of appsettings.json.
    /// PostgreSQL and SQLite are what StackDev can prepare; anything else is reported as "none" (the user sets it up) with the reason.
    /// </summary>
    public static DatabaseHint DetectDatabase(string projectPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
        var csproj = Safe(() => File.ReadAllText(projectPath));
        var settings = string.Join("\n", new[] { "appsettings.json", "appsettings.Production.json" }
            .Select(f => Safe(() => File.ReadAllText(Path.Combine(dir, f)))));

        if (Regex.IsMatch(csproj, @"Npgsql|PostgreSQL", RegexOptions.IgnoreCase))
            return new(DatabaseKind.Postgres, "tu proyecto usa PostgreSQL (Npgsql)");
        if (Regex.IsMatch(csproj, @"EntityFrameworkCore\.Sqlite|Microsoft\.Data\.Sqlite|System\.Data\.SQLite", RegexOptions.IgnoreCase))
            return new(DatabaseKind.Sqlite, "tu proyecto usa SQLite");
        if (Regex.IsMatch(csproj, @"SqlServer|MySql|Pomelo|Oracle|MongoDB", RegexOptions.IgnoreCase))
            return new(DatabaseKind.None, "tu proyecto usa otro motor (SQL Server, MySQL…) que StackDev no instala; la cadena la pones en «Variables»");
        if (Regex.IsMatch(settings, @"Host\s*=|Server\s*=\s*[^;]+;.*Database", RegexOptions.IgnoreCase))
            return new(DatabaseKind.Postgres, "la cadena de conexión de appsettings.json parece de PostgreSQL");
        if (Regex.IsMatch(settings, @"Data Source\s*=.*\.(db|sqlite)", RegexOptions.IgnoreCase))
            return new(DatabaseKind.Sqlite, "la cadena de conexión de appsettings.json es de SQLite");
        return new(DatabaseKind.Sqlite, "no encontré una base de datos en el proyecto; SQLite es lo más simple");
    }

    private static string Safe(Func<string> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
