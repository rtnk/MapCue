namespace MapCue.Domain.Secrets;

/// <summary>
/// A resolved secret. <see cref="ToString"/> never returns the value, so accidental interpolation
/// into logs or exceptions does not leak it. Use <see cref="Reveal"/> only at the process boundary.
/// </summary>
public sealed class SecretValue
{
    public const string Mask = "***";

    private readonly string _value;

    public SecretValue(SecretRef source, string value)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(value);
        Source = source;
        _value = value;
    }

    public SecretRef Source { get; }

    public string Reveal() => _value;

    public override string ToString() => Mask;
}
