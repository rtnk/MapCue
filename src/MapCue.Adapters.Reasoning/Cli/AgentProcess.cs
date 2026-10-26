using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MapCue.Adapters.Reasoning.Cli;

/// <summary>A process to run: exact argument vector (no shell), complete environment (nothing inherited) and stdin text.</summary>
public sealed record AgentProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string? StandardInput,
    TimeSpan Timeout,
    string? WorkingDirectory = null,
    int MaxOutputBytes = AgentProcessSpec.DefaultMaxOutputBytes)
{
    public const int DefaultMaxOutputBytes = 1024 * 1024;
}

public enum AgentProcessOutcome
{
    Exited,
    TimedOut,
    FailedToStart,
    OutputTooLarge,
}

public sealed record AgentProcessResult(AgentProcessOutcome Outcome, int? ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs agent processes; replaceable in tests to observe exactly what would be started.</summary>
public interface IAgentProcessLauncher
{
    Task<AgentProcessResult> RunAsync(AgentProcessSpec spec, CancellationToken ct);
}

/// <summary>
/// Starts a process with a clean environment and a hard timeout; on timeout, cancellation or oversized output the whole process
/// tree is killed. Never goes through a shell.
/// </summary>
public sealed class AgentProcessLauncher : IAgentProcessLauncher
{
    public async Task<AgentProcessResult> RunAsync(AgentProcessSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var info = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty,
        };
        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Clear();
        foreach (var (name, value) in spec.Environment)
        {
            info.Environment[name] = value;
        }

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                return new(AgentProcessOutcome.FailedToStart, null, string.Empty, string.Empty);
            }
        }
        catch (Win32Exception ex)
        {
            return new(AgentProcessOutcome.FailedToStart, null, string.Empty, ex.Message);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(spec.Timeout);
        var stdout = ReadLimitedAsync(process.StandardOutput, spec.MaxOutputBytes, timeout);
        var stderr = ReadLimitedAsync(process.StandardError, 64 * 1024, null);
        try
        {
            if (spec.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), timeout.Token);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            return output.TooLarge
                ? new(AgentProcessOutcome.OutputTooLarge, null, string.Empty, string.Empty)
                : new(AgentProcessOutcome.Exited, process.ExitCode, output.Text, (await stderr).Text);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            Kill(process);
            ct.ThrowIfCancellationRequested();
            var output = await stdout;
            return output.TooLarge
                ? new(AgentProcessOutcome.OutputTooLarge, null, string.Empty, string.Empty)
                : ex is IOException && process.HasExited
                    ? new(AgentProcessOutcome.Exited, process.ExitCode, output.Text, string.Empty)
                    : new(AgentProcessOutcome.TimedOut, null, string.Empty, string.Empty);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static async Task<(string Text, bool TooLarge)> ReadLimitedAsync(StreamReader reader, int maxBytes, CancellationTokenSource? cancelOnOverflow)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        var bytes = 0;
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
            {
                bytes += Encoding.UTF8.GetByteCount(buffer, 0, read);
                if (bytes > maxBytes)
                {
                    await (cancelOnOverflow?.CancelAsync() ?? Task.CompletedTask);
                    return (string.Empty, true);
                }

                builder.Append(buffer, 0, read);
            }
        }
        catch (IOException)
        {
            // The process was killed.
        }
        catch (ObjectDisposedException)
        {
            // The process was disposed.
        }

        return (builder.ToString(), false);
    }
}
