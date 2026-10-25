using MapCue.Application.Ports;

namespace MapCue.Adapters.TapCue;

/// <summary>
/// A TapCue call failed. <see cref="Dispatched"/> is true when the request may already have reached TapCue
/// (so a state-changing effect cannot be ruled out). Messages never contain flow env values.
/// </summary>
public class TapCueException : Exception
{
    public TapCueException()
    {
    }

    public TapCueException(string message)
        : base(message)
    {
    }

    public TapCueException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TapCueException(DeviceErrorKind kind, string message, bool dispatched = false, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Dispatched = dispatched;
    }

    public DeviceErrorKind Kind { get; } = DeviceErrorKind.Unknown;

    public bool Dispatched { get; }
}
