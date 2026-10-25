namespace MapCue.Domain;

/// <summary>Opaque device identifier as reported by TapCue (serial, emulator name or simulator UDID).</summary>
public readonly record struct DeviceId
{
    public DeviceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
