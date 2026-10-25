using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using MapCue.Application.Ports;
using Microsoft.Extensions.Logging;

namespace MapCue.Infrastructure.Artifacts.Logging;

/// <summary>
/// Structured logger writing one JSON object per line. Every string that leaves the process
/// (message, structured values, exception text) passes through <see cref="IRedactor"/> first.
/// </summary>
public sealed class JsonLinesLoggerProvider(TextWriter writer, IRedactor redactor, IClock clock, LogLevel minimumLevel = LogLevel.Information) : ILoggerProvider
{
    private readonly Lock _gate = new();

    internal LogLevel MinimumLevel { get; } = minimumLevel;

    internal DateTimeOffset Now => clock.UtcNow;

    public ILogger CreateLogger(string categoryName) => new JsonLinesLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            writer.Flush();
        }
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            writer.WriteLine(line);
            writer.Flush();
        }
    }

    private string Redact(string text) => redactor.Redact(text, RedactionContext.Log).Text;

    private sealed class JsonLinesLogger(JsonLinesLoggerProvider owner, string category) : ILogger
    {
        private static readonly JsonWriterOptions _writerOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, _writerOptions))
            {
                json.WriteStartObject();
                json.WriteString("ts", owner.Now.ToString("O", CultureInfo.InvariantCulture));
                json.WriteString("level", logLevel.ToString());
                json.WriteString("category", category);
                if (eventId.Id != 0 || eventId.Name is not null)
                {
                    json.WriteNumber("eventId", eventId.Id);
                    if (eventId.Name is not null)
                    {
                        json.WriteString("eventName", eventId.Name);
                    }
                }

                json.WriteString("message", owner.Redact(formatter(state, exception)));
                if (state is IReadOnlyList<KeyValuePair<string, object?>> properties)
                {
                    json.WriteStartObject("props");
                    foreach (var (key, value) in properties)
                    {
                        if (key == "{OriginalFormat}")
                        {
                            // Templates are usually constants, but an interpolated "template" carries values too.
                            json.WriteString("template", owner.Redact(value?.ToString() ?? string.Empty));
                            continue;
                        }

                        WriteValue(json, key, value);
                    }

                    json.WriteEndObject();
                }

                if (exception is not null)
                {
                    json.WriteStartObject("exception");
                    json.WriteString("type", exception.GetType().FullName);
                    json.WriteString("message", owner.Redact(exception.Message));
                    json.WriteString("detail", owner.Redact(exception.ToString()));
                    json.WriteEndObject();
                }

                json.WriteEndObject();
            }

            owner.Write(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        }

        private void WriteValue(Utf8JsonWriter json, string key, object? value)
        {
            switch (value)
            {
                case null:
                    json.WriteNull(key);
                    break;
                case bool b:
                    json.WriteBoolean(key, b);
                    break;
                case int or long or short or byte or uint or ulong or ushort or sbyte:
                    json.WriteNumber(key, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    break;
                case double or float or decimal:
                    json.WriteNumber(key, Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                    break;
                default:
                    json.WriteString(key, owner.Redact(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
                    break;
            }
        }
    }
}
