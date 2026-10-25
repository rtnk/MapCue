namespace MapCue.Application.Ports;

/// <summary>
/// Removes sensitive values before any persistent write (logs, artifacts, reports; architecture §10.3).
/// </summary>
public interface IRedactor
{
    RedactedContent Redact(string content, RedactionContext context);

    /// <summary>Registers a known sensitive value (e.g. a resolved secret) so that it is always redacted.</summary>
    void RegisterSensitiveValue(string value);
}

/// <summary>Where the content is going to be written; used for audit, not for weakening rules.</summary>
public sealed record RedactionContext(string Sink)
{
    public static RedactionContext Log { get; } = new("log");
}

public sealed record RedactedContent(string Text, int RedactionCount, IReadOnlyList<string> RulesApplied);
