namespace MapCue.Domain;

/// <summary>How profile data may be affected by exploration. <see cref="SharedReadOnly"/> is the safe default.</summary>
public enum DataIsolation
{
    SharedReadOnly,
    Disposable,
}
