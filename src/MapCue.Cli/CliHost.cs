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

    public CliHost(TextWriter logWriter, IClock? clock = null, LogLevel? minimumLevel = null)
    {
        ArgumentNullException.ThrowIfNull(logWriter);
        Clock = clock ?? new SystemClock();
        Redactor = new PatternRedactor();
        var level = minimumLevel ?? ParseLevel(Environment.GetEnvironmentVariable(LogLevelVariable));
        _loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(level)
            .AddProvider(new JsonLinesLoggerProvider(logWriter, Redactor, Clock, level)));
    }

    public IClock Clock { get; }

    public IRedactor Redactor { get; }

    public ILogger CreateLogger(string category) => _loggerFactory.CreateLogger(category);

    public void Dispose() => _loggerFactory.Dispose();

    private static LogLevel ParseLevel(string? value) =>
        Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) && Enum.IsDefined(level) ? level : LogLevel.Warning;
}
