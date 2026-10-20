using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using StackDev.Core.Ui;

namespace StackDev.Gui;

/// <summary>
/// La "consola" de la ventana: los mensajes de la lógica van al panel de salida y las preguntas son diálogos.
/// Se puede llamar desde cualquier hilo (las acciones corren en segundo plano).
/// </summary>
public sealed class GuiUi : IConsolaUi
{
    private const int MaxBloques = 4000;
    private readonly Window _ventana;
    private readonly RichTextBox _salida;

    public GuiUi(Window ventana, RichTextBox salida)
    {
        _ventana = ventana;
        _salida = salida;
    }

    private Dispatcher D => _ventana.Dispatcher;

    public bool Interactiva => true;

    private static Brush ColorDe(Nivel n) => n switch
    {
        Nivel.Titulo => Pincel("#A5B4FC"),
        Nivel.Info => Pincel("#7DD3FC"),
        Nivel.Ok => Pincel("#4ADE80"),
        Nivel.Aviso => Pincel("#FBBF24"),
        Nivel.Error => Pincel("#F87171"),
        Nivel.Tenue => Pincel("#8B93A5"),
        _ => Pincel("#E6E8EE"),
    };

    private static Brush Pincel(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public void Escribir(string texto, Nivel nivel = Nivel.Normal)
    {
        D.InvokeAsync(() =>
        {
            foreach (var linea in texto.Split('\n'))
            {
                var p = new Paragraph(new Run(linea.TrimEnd('\r')) { Foreground = ColorDe(nivel) });
                _salida.Document.Blocks.Add(p);
            }
            while (_salida.Document.Blocks.Count > MaxBloques && _salida.Document.Blocks.FirstBlock is { } primero)
                _salida.Document.Blocks.Remove(primero);
            _salida.ScrollToEnd();
        });
    }

    public void EscribirInline(string texto)
    {
        D.InvokeAsync(() =>
        {
            if (_salida.Document.Blocks.LastBlock is Paragraph ultimo) ultimo.Inlines.Add(new Run(texto) { Foreground = ColorDe(Nivel.Tenue) });
            else _salida.Document.Blocks.Add(new Paragraph(new Run(texto)));
            _salida.ScrollToEnd();
        });
    }

    public bool Confirmar(string pregunta, bool porDefecto = true) =>
        D.Invoke(() => Dialogos.Confirmar(_ventana, pregunta, porDefecto));

    public string? Preguntar(string pregunta, string? porDefecto = null) =>
        D.Invoke(() => Dialogos.Preguntar(_ventana, pregunta, porDefecto));

    public int Elegir(string titulo, IReadOnlyList<string> opciones, int porDefecto = -1) =>
        D.Invoke(() => Dialogos.Elegir(_ventana, titulo, opciones, porDefecto));

    public void Limpiar() => D.InvokeAsync(() => _salida.Document.Blocks.Clear());
}
