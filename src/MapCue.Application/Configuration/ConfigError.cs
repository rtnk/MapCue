namespace MapCue.Application.Configuration;

/// <summary>
/// A configuration problem. <see cref="Message"/> never contains configuration values,
/// only keys and expectations, so it is safe to print and log.
/// </summary>
public sealed record ConfigError(string Code, string Path, string Message)
{
    public override string ToString() => $"{Code} {Path}: {Message}";
}

public static class ConfigErrorCodes
{
    public const string FileUnreadable = "CFG001";
    public const string SyntaxError = "CFG002";
    public const string SchemaVersion = "CFG003";
    public const string MissingAppIds = "CFG010";
    public const string MissingEnvironments = "CFG011";
    public const string UnsupportedEnvironment = "CFG012";
    public const string InvalidAllowlistEntry = "CFG013";
    public const string BudgetOutOfRange = "CFG020";
    public const string InvalidDuration = "CFG021";
    public const string InvalidAgentRuntime = "CFG030";
    public const string LocalAgentNotAllowed = "CFG031";
    public const string InvalidProfile = "CFG040";
    public const string ProfileOutsideAllowlist = "CFG041";
    public const string LiteralInputValue = "CFG050";
    public const string InvalidSecretRef = "CFG051";
    public const string InvalidTapCueSetting = "CFG060";
    public const string ForbiddenPassEnvironment = "CFG061";
}

public sealed record ConfigValidationResult(MapCueSettings? Settings, IReadOnlyList<ConfigError> Errors)
{
    public bool IsValid => Settings is not null && Errors.Count == 0;

    public static ConfigValidationResult Failure(params ConfigError[] errors) => new(null, errors);
}
