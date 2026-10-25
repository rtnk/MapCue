namespace MapCue.Adapters.TapCue.Processes;

/// <summary>
/// Builds the environment of a TapCue child process from scratch (architecture §10.3, G-06/R-13).
/// Only a small allowlist of variables needed by the JVM, adb and Xcode is copied from the parent,
/// plus names the operator lists explicitly. <c>MAESTRO_*</c> variables are never passed: TapCue echoes
/// all of them back in <c>run</c> results.
/// </summary>
public static class ProcessEnvironment
{
    /// <summary>Variables copied from the parent when present. JVM option variables are deliberately absent.</summary>
    public static IReadOnlyList<string> DefaultAllowlist { get; } =
    [
        "PATH",
        "HOME",
        "USER",
        "LOGNAME",
        "TMPDIR",
        "LANG",
        "LC_ALL",
        "JAVA_HOME",
        "ANDROID_HOME",
        "ANDROID_SDK_ROOT",
        "ANDROID_USER_HOME",
        "ANDROID_ADB_SERVER_PORT",
        "XDG_STATE_HOME",
        "DEVELOPER_DIR",
    ];

    public const string ForbiddenPrefix = "MAESTRO_";

    public static bool IsForbidden(string name) =>
        name.StartsWith(ForbiddenPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the child environment: allowlisted parent variables only, never <c>MAESTRO_*</c>.</summary>
    public static IReadOnlyDictionary<string, string> Build(IEnumerable<string> extraNames, Func<string, string?> parent)
    {
        ArgumentNullException.ThrowIfNull(extraNames);
        ArgumentNullException.ThrowIfNull(parent);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in DefaultAllowlist.Concat(extraNames))
        {
            if (IsForbidden(name) || result.ContainsKey(name))
            {
                continue;
            }

            var value = parent(name);
            if (!string.IsNullOrEmpty(value))
            {
                result[name] = value;
            }
        }

        return result;
    }

    public static IReadOnlyDictionary<string, string> FromCurrentProcess(IEnumerable<string> extraNames) =>
        Build(extraNames, Environment.GetEnvironmentVariable);
}
