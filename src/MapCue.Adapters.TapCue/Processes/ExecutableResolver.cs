namespace MapCue.Adapters.TapCue.Processes;

/// <summary>Resolves the TapCue executable from configuration: an explicit path, or a bare name looked up on <c>PATH</c>.</summary>
public static class ExecutableResolver
{
    public static string? Resolve(string executable, string? pathVariable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (executable.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) || executable.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            var full = Path.GetFullPath(executable);
            return File.Exists(full) ? full : null;
        }

        foreach (var dir in (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, executable);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
