using System.Text.Json;
using System.Text.Json.Serialization;

namespace StackDev.Core.Detection.Profiles;

/// <summary>
/// How to recognise one kind of project and what to run for it, written as data (JSON) so that adding a language
/// or a framework needs no C#. A profile fills the "api" or "web" slot of the project configuration.
/// </summary>
public sealed class ProjectProfile
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>Shown to the user, e.g. "Node · NestJS".</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>"api" or "web": the slot of the project configuration this profile fills.</summary>
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("match")] public ProfileMatch Match { get; set; } = new();
    /// <summary>Default address once running (a port found by <see cref="PortFrom"/> replaces its number).</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    /// <summary>Path opened in the browser for an api (documentation page), e.g. "/docs".</summary>
    [JsonPropertyName("open")] public string Open { get; set; } = "";
    [JsonPropertyName("portFrom")] public List<PortRule> PortFrom { get; set; } = new();
    [JsonPropertyName("commands")] public ProfileCommands Commands { get; set; } = new();
    [JsonPropertyName("tasks")] public List<ProfileTask> Tasks { get; set; } = new();
}

/// <summary>
/// A profile matches a file when the file's name is one of <see cref="Files"/>, every <see cref="Contains"/> pattern is found
/// in its text, no <see cref="NotContains"/> pattern is, and every <see cref="AlsoFiles"/> entry exists next to it.
/// Patterns are case-insensitive regular expressions.
/// </summary>
public sealed class ProfileMatch
{
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();
    [JsonPropertyName("contains")] public List<string> Contains { get; set; } = new();
    [JsonPropertyName("notContains")] public List<string> NotContains { get; set; } = new();
    [JsonPropertyName("alsoFiles")] public List<string> AlsoFiles { get; set; } = new();
}

/// <summary>Reads a port from a file of the project (first capture group), e.g. <c>--port[= ](\d+)</c> in package.json.</summary>
public sealed class PortRule
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("regex")] public string Regex { get; set; } = "";
}

public sealed class ProfileCommands
{
    [JsonPropertyName("install")] public CommandSpec? Install { get; set; }
    [JsonPropertyName("start")] public CommandSpec? Start { get; set; }
    [JsonPropertyName("test")] public CommandSpec? Test { get; set; }
    [JsonPropertyName("build")] public CommandSpec? Build { get; set; }
}

public sealed class ProfileTask
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    /// <summary>File (relative to the project folder) that must exist for the task to be offered.</summary>
    [JsonPropertyName("when")] public string? When { get; set; }
}

/// <summary>
/// A command written either as a plain string or as a list of alternatives <c>{ "when": "file", "command": "..." }</c>:
/// the first alternative whose file exists wins and one without "when" is the default.
/// Placeholders: <c>{pm}</c> is the Node package manager (npm, pnpm or yarn).
/// </summary>
[JsonConverter(typeof(CommandSpecConverter))]
public sealed class CommandSpec
{
    public List<(string? When, string Command)> Options { get; } = new();

    public static CommandSpec Of(string command)
    {
        var s = new CommandSpec();
        s.Options.Add((null, command));
        return s;
    }

    /// <summary>The command to use in <paramref name="folder"/>, or null when no alternative applies.</summary>
    public string? Resolve(string folder, string packageManager)
    {
        foreach (var (when, command) in Options)
        {
            if (when is not null && !System.IO.File.Exists(Path.Combine(folder, when))) continue;
            return command.Replace("{pm}", packageManager, StringComparison.Ordinal);
        }
        return null;
    }
}

public sealed class CommandSpecConverter : JsonConverter<CommandSpec>
{
    public override CommandSpec? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return CommandSpec.Of(reader.GetString() ?? "");
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("A command is a string or a list of alternatives.");
        var spec = new CommandSpec();
        using var doc = JsonDocument.ParseValue(ref reader);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var command = e.TryGetProperty("command", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(command)) throw new JsonException("An alternative needs a \"command\".");
            spec.Options.Add((e.TryGetProperty("when", out var w) ? w.GetString() : null, command));
        }
        return spec;
    }

    public override void Write(Utf8JsonWriter writer, CommandSpec value, JsonSerializerOptions options)
    {
        if (value.Options.Count == 1 && value.Options[0].When is null) { writer.WriteStringValue(value.Options[0].Command); return; }
        writer.WriteStartArray();
        foreach (var (when, command) in value.Options)
        {
            writer.WriteStartObject();
            if (when is not null) writer.WriteString("when", when);
            writer.WriteString("command", command);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
