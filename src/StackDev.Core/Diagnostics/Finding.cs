namespace StackDev.Core.Diagnostics;

public enum Severity { Ok, Info, Warning, Error }

/// <summary>A diagnostic result; if it can be fixed, it carries the button text and what to do.</summary>
public sealed record Finding(
    Severity Severity, string Area, string Title, string Detail = "",
    string? ActionTitle = null, Func<Session, bool>? Repair = null);
