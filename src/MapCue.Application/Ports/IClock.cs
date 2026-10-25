namespace MapCue.Application.Ports;

/// <summary>Source of time. Always injected; tests use a fixed or manual clock.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
