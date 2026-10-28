namespace StackDev.Core.Ui;

public enum Level { Normal, Title, Info, Ok, Warning, Error, Muted }

/// <summary>
/// The only thing the logic needs from the screen: write messages and ask questions.
/// The Spectre.Console interface and the headless-command one implement it separately.
/// </summary>
public interface IConsoleUi
{
    void Write(string text, Level level = Level.Normal);

    /// <summary>Writes without a line break (the dots of "Waiting...").</summary>
    void WriteInline(string text);

    bool Confirm(string question, bool byDefault = true);

    /// <summary>Asks for text. Returns null if the user types nothing.</summary>
    string? Ask(string question, string? byDefault = null);

    /// <summary>Returns the chosen index, or -1 if cancelled.</summary>
    int Choose(string title, IReadOnlyList<string> options, int byDefault = -1);

    /// <summary>Is there a person who can answer? If not, the default values are used.</summary>
    bool Interactive { get; }
}
