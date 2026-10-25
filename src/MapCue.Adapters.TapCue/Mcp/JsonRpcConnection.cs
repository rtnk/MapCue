using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MapCue.Application.Ports;

namespace MapCue.Adapters.TapCue.Mcp;

/// <summary>Direction of a transcript line: client to server or server to client.</summary>
public enum TranscriptDirection
{
    ClientToServer,
    ServerToClient,
}

/// <summary>Receives every raw JSON-RPC line (for fixtures and evidence). Implementations must redact before persisting.</summary>
public interface IMcpTranscriptSink
{
    void Write(TranscriptDirection direction, string line);
}

/// <summary>The server answered a request with a JSON-RPC error object.</summary>
public sealed class McpRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// JSON-RPC 2.0 over line-delimited STDIO (MCP stdio transport): one JSON message per line, UTF-8.
/// Requests are correlated by id. Server-initiated requests are answered (<c>ping</c>) or rejected with
/// "method not found"; notifications are ignored. End of stream or an oversized line closes the connection
/// and fails every pending request.
/// </summary>
internal sealed class JsonRpcConnection : IAsyncDisposable
{
    public const int DefaultMaxLineChars = 32 * 1024 * 1024;

    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly IMcpTranscriptSink? _transcript;
    private readonly int _maxLineChars;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _readLoop;
    private readonly char[] _buffer = new char[16 * 1024];
    private int _bufferPos;
    private int _bufferLen;
    private long _nextId;
    private volatile string? _closedReason;

    public JsonRpcConnection(TextReader reader, TextWriter writer, IMcpTranscriptSink? transcript = null, int maxLineChars = DefaultMaxLineChars)
    {
        _reader = reader;
        _writer = writer;
        _transcript = transcript;
        _maxLineChars = maxLineChars;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public bool IsClosed => _closedReason is not null;

    public string? ClosedReason => _closedReason;

    /// <summary>
    /// Sends a request and waits for its response. Throws <see cref="TapCueException"/> with
    /// <see cref="DeviceErrorKind.TransportClosed"/> when the stream ends; <c>Dispatched</c> tells whether the
    /// request line was fully written before that happened.
    /// </summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null)
            {
                message["params"] = parameters;
            }

            await WriteAsync(message, ct).ConfigureAwait(false);
            if (_closedReason is { } reason)
            {
                // Closed between registration and write completion; Close may have missed this request.
                tcs.TrySetException(new TapCueException(DeviceErrorKind.TransportClosed, $"MCP connection closed ({reason}).", dispatched: true));
            }

            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return WriteAsync(message, ct);
    }

    public async ValueTask DisposeAsync()
    {
        Close("connection disposed");
        await _closing.CancelAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The reader is blocked on a pipe that the owner kills right after.
        }

        _writeLock.Dispose();
        _closing.Dispose();
    }

    private async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        if (_closedReason is { } reason)
        {
            throw new TapCueException(DeviceErrorKind.TransportClosed, $"MCP connection is closed ({reason}).", dispatched: false);
        }

        var line = message.ToJsonString();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _transcript?.Write(TranscriptDirection.ClientToServer, line);
            await _writer.WriteAsync((line + "\n").AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The line may be partially written; the server may or may not have read it.
            Close("stdin closed");
            throw new TapCueException(DeviceErrorKind.TransportClosed, "MCP server stdin is closed.", dispatched: true, ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var line = await ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    Close("server closed stdout");
                    return;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                _transcript?.Write(TranscriptDirection.ServerToClient, line);
                await DispatchAsync(line).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
        {
            Close(ex is InvalidDataException ? ex.Message : "server stdout failed");
        }
    }

    private async Task<string?> ReadLineAsync()
    {
        var builder = new StringBuilder();
        while (true)
        {
            if (_bufferPos == _bufferLen)
            {
                _bufferLen = await _reader.ReadAsync(_buffer.AsMemory(), _closing.Token).ConfigureAwait(false);
                _bufferPos = 0;
                if (_bufferLen == 0)
                {
                    return builder.Length > 0 ? builder.ToString() : null;
                }
            }

            var span = _buffer.AsSpan(_bufferPos, _bufferLen - _bufferPos);
            var newline = span.IndexOf('\n');
            var take = newline < 0 ? span.Length : newline;
            if (builder.Length + take > _maxLineChars)
            {
                throw new InvalidDataException($"MCP message exceeds {_maxLineChars} characters.");
            }

            builder.Append(span[..take]);
            _bufferPos += newline < 0 ? take : take + 1;
            if (newline >= 0)
            {
                if (builder.Length > 0 && builder[^1] == '\r')
                {
                    builder.Length--;
                }

                return builder.ToString();
            }
        }
    }

    private async Task DispatchAsync(string line)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            // Not JSON-RPC (stray output on stdout). Ignored; a missing response surfaces as a timeout.
            return;
        }

        if (node is not JsonObject message)
        {
            return;
        }

        var hasMethod = message.TryGetPropertyValue("method", out var methodNode) && methodNode is JsonValue;
        var hasId = message.TryGetPropertyValue("id", out var idNode) && idNode is not null;
        if (hasMethod && hasId)
        {
            await AnswerServerRequestAsync(idNode!.DeepClone(), methodNode!.GetValue<string>()).ConfigureAwait(false);
            return;
        }

        if (hasMethod || !hasId || !TryGetId(idNode!, out var id) || !_pending.TryGetValue(id, out var tcs))
        {
            return;
        }

        if (message.TryGetPropertyValue("error", out var error) && error is JsonObject errorObject)
        {
            var code = errorObject["code"] is JsonValue c && c.TryGetValue<int>(out var v) ? v : 0;
            var text = errorObject["message"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : "JSON-RPC error";
            tcs.TrySetException(new McpRpcException(code, text));
            return;
        }

        tcs.TrySetResult(message["result"]?.DeepClone());
    }

    private async Task AnswerServerRequestAsync(JsonNode id, string method)
    {
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        if (method == "ping")
        {
            reply["result"] = new JsonObject();
        }
        else
        {
            reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
        }

        try
        {
            await WriteAsync(reply, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TapCueException)
        {
            // Connection closed; pending requests are failed by Close.
        }
    }

    private static bool TryGetId(JsonNode node, out long id)
    {
        id = 0;
        if (node is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<long>(out id))
        {
            return true;
        }

        return value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    private void Close(string reason)
    {
        if (_closedReason is null)
        {
            _closedReason = reason;
        }

        foreach (var (_, tcs) in _pending)
        {
            tcs.TrySetException(new TapCueException(DeviceErrorKind.TransportClosed, $"MCP connection closed ({reason}).", dispatched: true));
        }
    }
}
