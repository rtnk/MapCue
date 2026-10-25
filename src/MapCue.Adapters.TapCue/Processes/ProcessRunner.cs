using System.Diagnostics;
using System.Text;

namespace MapCue.Adapters.TapCue.Processes;

/// <summary>Result of a run-to-completion child process. <see cref="ExitCode"/> is null when the process was killed on timeout.</summary>
public sealed record ProcessResult(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool OutputTruncated, TimeSpan Duration);

/// <summary>Runs a short-lived child process with a hard timeout; on timeout or cancellation the whole process tree is killed.</summary>
public static class ProcessRunner
{
    public const int DefaultMaxOutputChars = 64 * 1024 * 1024;

    // A daemon started by the child (e.g. an adb server) may inherit the pipes and keep them open after the
    // child exits; output is then collected for at most this long instead of waiting for end-of-stream.
    private static readonly TimeSpan _drainGrace = TimeSpan.FromSeconds(2);

    public static async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct, int maxOutputChars = DefaultMaxOutputChars)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var stopwatch = Stopwatch.StartNew();
        using var child = ChildProcess.Start(spec);
        child.StandardInput.Close();
        var stdout = new StringBuilder();
        var stdoutLock = new Lock();
        var truncated = false;
        var pump = Task.Run(async () =>
        {
            var buffer = new char[8192];
            try
            {
                int read;
                while ((read = await child.StandardOutput.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    lock (stdoutLock)
                    {
                        var room = maxOutputChars - stdout.Length;
                        if (room > 0)
                        {
                            stdout.Append(buffer, 0, Math.Min(room, read));
                        }

                        truncated |= read > room;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Pipe closed by the kill; keep what was read.
            }
        }, CancellationToken.None);

        string Output()
        {
            lock (stdoutLock)
            {
                return stdout.ToString();
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var exited = await child.WaitForExitAsync(timeout).WaitAsync(timeoutCts.Token).ContinueWith(t => t.IsCompletedSuccessfully && t.Result, TaskScheduler.Default).ConfigureAwait(false);
        if (!exited)
        {
            child.Kill();
            await child.WaitForExitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new ProcessResult(null, Output(), child.StderrTail, TimedOut: true, truncated, stopwatch.Elapsed);
        }

        await Task.WhenAny(Task.WhenAll(pump, child.StderrCompletion), Task.Delay(_drainGrace, CancellationToken.None)).ConfigureAwait(false);
        return new ProcessResult(child.ExitCode, Output(), child.StderrTail, TimedOut: false, truncated, stopwatch.Elapsed);
    }
}
