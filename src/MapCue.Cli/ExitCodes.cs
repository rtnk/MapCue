namespace MapCue.Cli;

/// <summary>Process exit codes of <c>mapcue</c>. Stable contract for scripts and CI.</summary>
public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>Invalid command line (System.CommandLine parse error).</summary>
    public const int UsageError = 1;

    /// <summary>Configuration file missing, unreadable or invalid.</summary>
    public const int ConfigInvalid = 2;

    /// <summary>The command exists but is not implemented yet.</summary>
    public const int NotImplemented = 3;
}
