using MapCue.Application.Ports;

namespace MapCue.Testing;

/// <summary>Deterministic clock: starts at a fixed instant and only moves when told to.</summary>
public sealed class ManualClock(DateTimeOffset start) : IClock
{
    public static readonly DateTimeOffset DefaultStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public ManualClock()
        : this(DefaultStart)
    {
    }

    public DateTimeOffset UtcNow { get; private set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Deterministic ids: <c>{prefix}_0001</c>, <c>{prefix}_0002</c>, … counted per prefix.</summary>
public sealed class SequentialIdGenerator : IIdGenerator
{
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);

    public string NewId(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var next = _counters.GetValueOrDefault(prefix) + 1;
        _counters[prefix] = next;
        return $"{prefix}_{next:D4}";
    }
}
