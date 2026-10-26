using MapCue.Adapters.TapCue;
using MapCue.Application.Ports;
using MapCue.Application.Time;
using MapCue.Infrastructure.Artifacts.Logging;
using MapCue.Infrastructure.Artifacts.Redaction;
using Microsoft.Extensions.Logging;

namespace MapCue.Cli;

/// <summary>
/// Composition root of the CLI. Logs are JSON lines on stderr, always redacted; the level comes from
/// <c>MAPCUE_LOG_LEVEL</c> (default Warning). Adapters are wired here in later steps.
/// </summary>
public sealed class CliHost : IDisposable
{
    public const string LogLevelVariable = "MAPCUE_LOG_LEVEL";

    private readonly ILoggerFactory _loggerFactory;
    private readonly IReadOnlyDictionary<string, string>? _environment;

    public CliHost(
        TextWriter logWriter,
        IClock? clock = null,
        LogLevel? minimumLevel = null,
        IReadOnlyDictionary<string, string>? environment = null,
        Func<TapCueAdapterOptions, Func<string, string?>, IDeviceAutomation>? deviceFactory = null)
    {
        ArgumentNullException.ThrowIfNull(logWriter);
        _environment = environment;
        DeviceFactory = deviceFactory;
        Clock = clock ?? new SystemClock();
        Redactor = new PatternRedactor();
        var level = minimumLevel ?? ParseLevel(Environment(LogLevelVariable));
        _loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(level)
            .AddProvider(new JsonLinesLoggerProvider(logWriter, Redactor, Clock, level)));
    }

    public IClock Clock { get; }

    public IRedactor Redactor { get; }

    /// <summary>
    /// Device automation used by <c>mapcue explore</c> (options, input resolver); null = TapCue (<see cref="TapCueDeviceAutomation"/>).
    /// Tests inject a fake device here.
    /// </summary>
    public Func<TapCueAdapterOptions, Func<string, string?>, IDeviceAutomation>? DeviceFactory { get; }

    public ILogger CreateLogger(string category) => _loggerFactory.CreateLogger(category);

    /// <summary>Reads a variable of the process environment (or of the environment injected by tests).</summary>
    public string? Environment(string name) =>
        _environment is null ? System.Environment.GetEnvironmentVariable(name) : _environment.GetValueOrDefault(name);

    /// <summary>Names of all variables of the process environment (or of the injected one).</summary>
    public IEnumerable<string> EnvironmentNames() =>
        _environment?.Keys ?? System.Environment.GetEnvironmentVariables().Keys.Cast<string>();

    public void Dispose() => _loggerFactory.Dispose();

    private static LogLevel ParseLevel(string? value) =>
        Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) && Enum.IsDefined(level) ? level : LogLevel.Warning;
}
