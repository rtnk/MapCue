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

    /// <summary><c>mapcue doctor</c> found at least one failing prerequisite.</summary>
    public const int PrerequisitesFailed = 4;

    /// <summary>An input file other than the configuration (e.g. a <c>graph import</c> plan) is missing, unreadable or invalid.</summary>
    public const int InputInvalid = 5;

    /// <summary>The workspace is locked by another MapCue process or its database schema is newer or changed.</summary>
    public const int WorkspaceUnavailable = 6;

    /// <summary><c>mapcue explore</c>: the run did not start (preflight: device, setup flow, app in the foreground, secrets).</summary>
    public const int PreflightFailed = 7;

    /// <summary><c>mapcue explore</c>: the run stopped on an error or was cancelled by the operator.</summary>
    public const int RunFailed = 8;
}
