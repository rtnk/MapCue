namespace MapCue.Infrastructure.Sqlite;

/// <summary>The workspace is already used by another MapCue process (R-11: one writer per workspace).</summary>
public sealed class WorkspaceLockedException(string path, Exception inner)
    : IOException($"Workspace lock '{path}' is held by another MapCue process.", inner);

/// <summary>
/// Exclusive lock of a workspace (<c>.mapcue/lock</c>, architecture §11). Held for the lifetime of the process that writes the
/// database; the operating system releases it when the process dies, so a crash never leaves a stale lock.
/// </summary>
public sealed class WorkspaceLock : IDisposable
{
    private readonly FileStream _stream;

    private WorkspaceLock(FileStream stream, string path)
    {
        _stream = stream;
        Path = path;
    }

    public string Path { get; }

    public static WorkspaceLock Acquire(string workspaceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        Directory.CreateDirectory(workspaceDirectory);
        var path = System.IO.Path.Combine(workspaceDirectory, "lock");
        try
        {
            // FileShare.None maps to an exclusive advisory lock (flock) on Unix and a share-mode lock on Windows.
            return new WorkspaceLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), path);
        }
        catch (IOException ex)
        {
            throw new WorkspaceLockedException(path, ex);
        }
    }

    public void Dispose() => _stream.Dispose();
}
