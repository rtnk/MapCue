using System.Text.Json.Nodes;
using MapCue.Adapters.TapCue.Mcp;
using MapCue.Application.Ports;

namespace MapCue.Adapters.TapCue.Smoke;

/// <summary>Writes MCP lines as fixture JSONL (<c>{"dir","msg"}</c>), redacted before they reach the disk.</summary>
internal sealed class RedactingTranscriptFile(string path, IRedactor redactor) : IMcpTranscriptSink, IDisposable
{
    private readonly StreamWriter _writer = new(path, append: false) { AutoFlush = true };
    private readonly Lock _lock = new();

    public void Write(TranscriptDirection direction, string line)
    {
        var redacted = redactor.Redact(line, new RedactionContext("fixture")).Text;
        JsonNode? msg;
        try
        {
            msg = JsonNode.Parse(redacted);
        }
        catch (System.Text.Json.JsonException)
        {
            msg = JsonValue.Create(redacted);
        }

        var entry = new JsonObject { ["dir"] = direction == TranscriptDirection.ClientToServer ? "c2s" : "s2c", ["msg"] = msg };
        lock (_lock)
        {
            _writer.WriteLine(entry.ToJsonString());
        }
    }

    public void Dispose() => _writer.Dispose();
}
