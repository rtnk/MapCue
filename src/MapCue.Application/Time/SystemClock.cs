using MapCue.Application.Ports;

namespace MapCue.Application.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
