namespace StackDev.Core.Diagnostics;

public enum Severity { Ok, Info, Warning, Error }

/// <summary>Un resultado del diagnóstico; si se puede arreglar, trae el texto del botón y qué hacer.</summary>
public sealed record Finding(
    Severity Severity, string Area, string Title, string Detail = "",
    string? ActionTitle = null, Func<Session, bool>? Repair = null);
