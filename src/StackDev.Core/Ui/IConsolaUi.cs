namespace StackDev.Core.Ui;

public enum Nivel { Normal, Titulo, Info, Ok, Aviso, Error, Tenue }

/// <summary>
/// Lo único que la lógica necesita de la pantalla: escribir mensajes y hacer preguntas.
/// La interfaz con Spectre.Console y la de comandos sin interfaz la implementan por separado.
/// </summary>
public interface IConsolaUi
{
    void Escribir(string texto, Nivel nivel = Nivel.Normal);

    /// <summary>Escribe sin salto de línea (los puntos de "Esperando...").</summary>
    void EscribirInline(string texto);

    bool Confirmar(string pregunta, bool porDefecto = true);

    /// <summary>Pide un texto. Devuelve null si el usuario no escribe nada.</summary>
    string? Preguntar(string pregunta, string? porDefecto = null);

    /// <summary>Devuelve el índice elegido, o -1 si se cancela.</summary>
    int Elegir(string titulo, IReadOnlyList<string> opciones, int porDefecto = -1);

    /// <summary>¿Hay una persona que pueda contestar? Si no, se usan los valores por defecto.</summary>
    bool Interactiva { get; }
}
