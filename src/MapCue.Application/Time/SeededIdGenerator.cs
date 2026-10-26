using System.Globalization;
using MapCue.Application.Ports;
using MapCue.Domain.Graph;

namespace MapCue.Application.Time;

/// <summary>
/// Deterministic ids from a seed: <c>{prefix}_{hash(seed, prefix, n)}</c>, counted per prefix. Two runs with the same seed get the
/// same run, event and checkpoint ids (graph ids are derived from content anyway), so their stores can be compared exactly.
/// </summary>
public sealed class SeededIdGenerator(string seed) : IIdGenerator
{
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string Seed { get; } = seed ?? throw new ArgumentNullException(nameof(seed));

    public string NewId(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        int next;
        lock (_gate)
        {
            next = _counters.GetValueOrDefault(prefix) + 1;
            _counters[prefix] = next;
        }

        return ContentHash.ShortId(prefix, ContentHash.Of("mapcue.seeded-id/1", Seed, prefix, next.ToString(CultureInfo.InvariantCulture)));
    }
}
