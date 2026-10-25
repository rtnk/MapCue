using System.Globalization;
using MapCue.Application.Ports;

namespace MapCue.Application.Time;

/// <summary>Production id generator: time-ordered UUIDv7 using the injected clock.</summary>
public sealed class Version7IdGenerator(IClock clock) : IIdGenerator
{
    public string NewId(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}_{Guid.CreateVersion7(clock.UtcNow):N}");
    }
}
