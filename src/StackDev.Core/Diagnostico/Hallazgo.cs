namespace StackDev.Core.Diagnostico;

public enum Gravedad { Ok, Info, Aviso, Error }

/// <summary>Un resultado del diagnóstico; si se puede arreglar, trae el texto del botón y qué hacer.</summary>
public sealed record Hallazgo(
    Gravedad Gravedad, string Area, string Titulo, string Detalle = "",
    string? AccionTitulo = null, Func<Sesion, bool>? Reparar = null);
