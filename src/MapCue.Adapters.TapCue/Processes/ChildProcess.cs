using System.Diagnostics;
using System.Text;

namespace MapCue.Adapters.TapCue.Processes;

/// <summary>What to start: an executable with arguments (never through a shell) and an explicit, complete environment.</summary>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string? WorkingDirectory = null);

/// <summary>
/// A started child process with redirected standard streams. The environment is replaced, not inherited.
/// <see cref="Kill"/> terminates the whole process tree (TapCue is a launcher script running a JVM).
/// Standard error is drained continuously into a bounded tail so the child never blocks on a full pipe.
/// </summary>
public sealed class ChildProcess : IDisposable
{
    public const int StderrTailChars = 64 * 1024;

    private const int TextFileBusy = 26; // ETXTBSY on Linux and macOS

    private readonly Process _process;
    private readonly StringBuilder _stderrTail = new();
    private readonly Lock _stderrLock = new();
    private readonly Task _stderrPump;

    private ChildProcess(Process process)
    {
        _process = process;
        _stderrPump = Task.Run(PumpStderrAsync);
    }

    public int Id => _process.Id;

    public StreamWriter StandardInput => _process.StandardInput;

    public StreamReader StandardOutput => _process.StandardOutput;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode => HasExited ? _process.ExitCode : null;

    public static ChildProcess Start(ProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var info = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty,
        };
        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Replace the inherited environment completely (G-06): nothing reaches TapCue unless listed.
        info.Environment.Clear();
        foreach (var (name, value) in spec.Environment)
        {
            info.Environment[name] = value;
        }

        // ETXTBSY: an executable that was just written (an installer, a test writing a launcher script) can still be
        // open for writing in another process forked concurrently; exec fails until that process execs or exits.
        for (var attempt = 1; ; attempt++)
        {
            var process = new Process { StartInfo = info };
            try
            {
                process.Start();
                return new ChildProcess(process);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == TextFileBusy && attempt < 5)
            {
                process.Dispose();
                Thread.Sleep(50 * attempt);
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }
    }

    /// <summary>The last <see cref="StderrTailChars"/> characters written to standard error (not redacted).</summary>
    public string StderrTail
    {
        get
        {
            lock (_stderrLock)
            {
                return _stderrTail.ToString();
            }
        }
    }

    /// <summary>Kills the process and all of its descendants. Safe to call repeatedly.</summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Raced with exit or lacks permission on a descendant; the direct child is gone or going.
        }
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }

    private async Task PumpStderrAsync()
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await _process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (_stderrLock)
                {
                    _stderrTail.Append(buffer, 0, read);
                    if (_stderrTail.Length > StderrTailChars)
                    {
                        _stderrTail.Remove(0, _stderrTail.Length - StderrTailChars);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process went away; the tail keeps what was read.
        }
    }

    internal Task StderrCompletion => _stderrPump;
}
