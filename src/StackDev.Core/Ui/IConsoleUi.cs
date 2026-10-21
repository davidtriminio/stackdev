namespace StackDev.Core.Ui;

public enum Level { Normal, Title, Info, Ok, Warning, Error, Muted }

/// <summary>
/// Lo único que la lógica necesita de la pantalla: escribir mensajes y hacer preguntas.
/// La interfaz con Spectre.Console y la de comandos sin interfaz la implementan por separado.
/// </summary>
public interface IConsoleUi
{
    void Write(string text, Level level = Level.Normal);

    /// <summary>Escribe sin salto de línea (los puntos de "Esperando...").</summary>
    void WriteInline(string text);

    bool Confirm(string question, bool byDefault = true);

    /// <summary>Pide un texto. Devuelve null si el usuario no escribe nada.</summary>
    string? Ask(string question, string? byDefault = null);

    /// <summary>Devuelve el índice elegido, o -1 si se cancela.</summary>
    int Choose(string title, IReadOnlyList<string> options, int byDefault = -1);

    /// <summary>¿Hay una persona que pueda contestar? Si no, se usan los valores por defecto.</summary>
    bool Interactive { get; }
}
