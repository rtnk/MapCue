using System.Text.Json.Nodes;
using MapCue.Adapters.TapCue.Processes;
using MapCue.Application.Ports;

namespace MapCue.Adapters.TapCue.Mcp;

/// <summary>
/// One <c>tapcue mcp</c> child process with an initialized MCP session (ADR-0002 <c>TapCueMcpTransport</c>).
/// TapCue has no timeout or cancellation for tool calls (G-01), so a call that exceeds its budget kills the
/// whole process tree and the session becomes <see cref="IsBroken"/>; the owner starts a new one.
/// </summary>
internal sealed class TapCueMcpSession : IAsyncDisposable
{
    private readonly ChildProcess _process;
    private readonly JsonRpcConnection _connection;
    private readonly McpToolClient _client;
    private volatile bool _broken;

    private TapCueMcpSession(ChildProcess process, JsonRpcConnection connection)
    {
        _process = process;
        _connection = connection;
        _client = new McpToolClient(connection);
    }

    public McpServerInfo? ServerInfo { get; private set; }

    public IReadOnlyList<McpToolInfo> Tools { get; private set; } = [];

    public bool IsBroken => _broken || _connection.IsClosed || _process.HasExited;

    public int ProcessId => _process.Id;

    /// <summary>Last characters of TapCue's standard error (not redacted; callers redact before logging).</summary>
    public string StderrTail => _process.StderrTail;

    /// <summary>Starts the process, performs the handshake and verifies the required tools within <paramref name="timeout"/>.</summary>
    public static async Task<TapCueMcpSession> StartAsync(ProcessSpec spec, TimeSpan timeout, string clientVersion, IMcpTranscriptSink? transcript, CancellationToken ct)
    {
        ChildProcess process;
        try
        {
            process = ChildProcess.Start(spec);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            throw new TapCueException(DeviceErrorKind.TransportClosed, $"Cannot start TapCue MCP server: {ex.Message}", dispatched: false, ex);
        }

        var connection = new JsonRpcConnection(process.StandardOutput, process.StandardInput, transcript);
        var session = new TapCueMcpSession(process, connection);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            session.ServerInfo = await session._client.InitializeAsync(clientVersion, timeoutCts.Token).ConfigureAwait(false);
            session.Tools = await session._client.ListToolsAsync(timeoutCts.Token).ConfigureAwait(false);
            var problems = McpToolClient.VerifyRequiredTools(session.Tools);
            if (problems.Count > 0)
            {
                throw new TapCueException(DeviceErrorKind.UnsupportedVersion, "TapCue MCP server lacks required tools: " + string.Join("; ", problems));
            }

            return session;
        }
        catch (Exception ex)
        {
            session.Abort();
            await session.DisposeAsync().ConfigureAwait(false);
            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new TapCueException(DeviceErrorKind.Timeout, $"TapCue MCP handshake did not complete within {timeout.TotalSeconds:0.#} s.", dispatched: false, ex);
            }

            if (ex is TapCueException { Kind: DeviceErrorKind.TransportClosed } closed)
            {
                // Nothing device-related was sent yet.
                throw new TapCueException(DeviceErrorKind.TransportClosed, closed.Message, dispatched: false, ex);
            }

            if (ex is McpRpcException rpc)
            {
                throw new TapCueException(DeviceErrorKind.UnsupportedVersion, $"TapCue MCP handshake failed: {rpc.Message}", dispatched: false, ex);
            }

            throw;
        }
    }

    /// <summary>
    /// Calls a tool with a hard timeout. On timeout or cancellation the process tree is killed (TapCue cannot cancel)
    /// and a <see cref="TapCueException"/> with <c>Dispatched = true</c> is thrown, or the cancellation propagates.
    /// </summary>
    public async Task<McpToolResult> CallToolAsync(string name, JsonObject arguments, TimeSpan timeout, CancellationToken ct)
    {
        if (IsBroken)
        {
            throw new TapCueException(DeviceErrorKind.TransportClosed, "TapCue MCP session is not usable.", dispatched: false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await _client.CallToolAsync(name, arguments, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Abort();
            throw new TapCueException(DeviceErrorKind.Timeout, $"TapCue '{name}' did not answer within {timeout.TotalSeconds:0.#} s; the MCP server was killed.", dispatched: true);
        }
        catch (OperationCanceledException)
        {
            Abort();
            throw;
        }
        catch (TapCueException)
        {
            Abort();
            throw;
        }
        catch (McpRpcException ex)
        {
            throw new TapCueException(DeviceErrorKind.InvalidRequest, $"TapCue '{name}' JSON-RPC error {ex.Code}: {ex.Message}", dispatched: true, ex);
        }
    }

    /// <summary>Kills the process tree; the session can no longer be used.</summary>
    public void Abort()
    {
        _broken = true;
        _process.Kill();
    }

    public async ValueTask DisposeAsync()
    {
        var graceful = !_broken;
        _broken = true;
        if (graceful)
        {
            try
            {
                // Closing stdin lets TapCue release its device sessions and exit; the tree is killed afterwards anyway.
                _process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Already closed.
            }

            await _process.WaitForExitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }

        _process.Kill();
        await _connection.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
    }
}
