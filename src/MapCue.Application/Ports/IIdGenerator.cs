namespace MapCue.Application.Ports;

/// <summary>Generates identifiers. Always injected; tests use a sequential generator for determinism.</summary>
public interface IIdGenerator
{
    /// <summary>Returns a new identifier with the given prefix, e.g. <c>run_…</c>.</summary>
    string NewId(string prefix);
}
