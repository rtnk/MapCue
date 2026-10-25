using System.Text.RegularExpressions;
using MapCue.Contracts;
using MapCue.Domain;
using MapCue.Domain.Secrets;

namespace MapCue.Application.Configuration;

/// <summary>
/// Validates a raw configuration document and produces <see cref="MapCueSettings"/>.
/// Fail-closed: missing allowlists, non-test environments, literal inputs or out-of-range budgets are errors.
/// Error messages never echo configuration values (they may be secrets).
/// </summary>
public static partial class MapCueConfigValidator
{
    /// <summary>The only environment class MapCue accepts (architecture §10.1: no production mode).</summary>
    public const string TestEnvironment = "test";

    private const string GeneratorPrefix = "generated:";

    public static ConfigValidationResult Validate(MapCueConfigDocument? document)
    {
        var errors = new List<ConfigError>();
        if (document is null)
        {
            errors.Add(new(ConfigErrorCodes.SyntaxError, "$", "Configuration is empty."));
            return new(null, errors);
        }

        if (!string.Equals(document.SchemaVersion, SchemaVersions.Config, StringComparison.Ordinal))
        {
            errors.Add(new(ConfigErrorCodes.SchemaVersion, "schemaVersion", $"Must be '{SchemaVersions.Config}'."));
        }

        var workspace = string.IsNullOrWhiteSpace(document.Workspace) ? MapCueSettings.DefaultWorkspace : document.Workspace.Trim();
        var allowlist = ValidateAllowlist(document.Allowlist, errors);
        var budgets = ValidateBudgets(document.Budgets, errors);
        var agent = ValidateAgent(document.Agent, errors);
        var profiles = ValidateProfiles(document.Profiles, allowlist, errors);

        return errors.Count > 0
            ? new(null, errors)
            : new(new MapCueSettings(workspace, allowlist, budgets, agent, profiles), errors);
    }

    private static AllowlistSettings ValidateAllowlist(AllowlistDocument? doc, List<ConfigError> errors)
    {
        var appIds = Entries(doc?.AppIds, "allowlist.appIds", errors);
        if (appIds.Count == 0)
        {
            errors.Add(new(ConfigErrorCodes.MissingAppIds, "allowlist.appIds", "At least one allowed appId is required; MapCue refuses to start without an app allowlist."));
        }

        var environments = Entries(doc?.Environments, "allowlist.environments", errors);
        if (environments.Count == 0)
        {
            errors.Add(new(ConfigErrorCodes.MissingEnvironments, "allowlist.environments", $"At least one allowed environment is required (only '{TestEnvironment}' is supported)."));
        }

        for (var i = 0; i < environments.Count; i++)
        {
            if (!string.Equals(environments[i], TestEnvironment, StringComparison.Ordinal))
            {
                errors.Add(new(ConfigErrorCodes.UnsupportedEnvironment, $"allowlist.environments[{i}]", $"Only '{TestEnvironment}' environments are supported; MapCue has no production mode."));
            }
        }

        return new AllowlistSettings(
            appIds,
            environments,
            Entries(doc?.DeviceIds, "allowlist.deviceIds", errors),
            Entries(doc?.ProfileIds, "allowlist.profileIds", errors));
    }

    private static List<string> Entries(List<string>? values, string path, List<ConfigError> errors)
    {
        var result = new List<string>();
        if (values is null)
        {
            return result;
        }

        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i]?.Trim();
            if (string.IsNullOrEmpty(value) || !AllowlistEntryPattern().IsMatch(value))
            {
                errors.Add(new(ConfigErrorCodes.InvalidAllowlistEntry, $"{path}[{i}]", "Entry must be a non-empty identifier (letters, digits, '.', '_', '-', '*')."));
                continue;
            }

            if (!result.Contains(value, StringComparer.Ordinal))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static Budgets ValidateBudgets(BudgetsDocument? doc, List<ConfigError> errors)
    {
        var d = Budgets.Default;
        if (doc is null)
        {
            return d;
        }

        return new Budgets(
            MaxDepth: Int(doc.MaxDepth, d.MaxDepth, 1, 64, "budgets.maxDepth", errors),
            MaxScreens: Int(doc.MaxScreens, d.MaxScreens, 1, 1_000, "budgets.maxScreens", errors),
            MaxStates: Int(doc.MaxStates, d.MaxStates, 1, 5_000, "budgets.maxStates", errors),
            MaxActions: Int(doc.MaxActions, d.MaxActions, 1, 10_000, "budgets.maxActions", errors),
            MaxDuration: Duration(doc.MaxDuration, d.MaxDuration, TimeSpan.FromSeconds(1), TimeSpan.FromHours(24), "budgets.maxDuration", errors),
            MaxRetries: Int(doc.MaxRetries, d.MaxRetries, 0, 5, "budgets.maxRetries", errors),
            MaxListRowsPerTemplate: Int(doc.MaxListRowsPerTemplate, d.MaxListRowsPerTemplate, 1, 20, "budgets.maxListRowsPerTemplate", errors),
            UiSettleTimeout: Duration(doc.UiSettleTimeout, d.UiSettleTimeout, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(60), "budgets.uiSettleTimeout", errors),
            ActionTimeout: Duration(doc.ActionTimeout, d.ActionTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10), "budgets.actionTimeout", errors),
            ObserveTimeout: Duration(doc.ObserveTimeout, d.ObserveTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10), "budgets.observeTimeout", errors));
    }

    private static AgentSettings ValidateAgent(AgentDocument? doc, List<ConfigError> errors)
    {
        var d = AgentSettings.Default;
        if (doc is null)
        {
            return d;
        }

        var runtime = d.Runtime;
        switch (doc.Runtime)
        {
            case null or "container":
                runtime = AgentRuntime.Container;
                break;
            case "local":
                runtime = AgentRuntime.Local;
                break;
            default:
                errors.Add(new(ConfigErrorCodes.InvalidAgentRuntime, "agent.runtime", "Must be 'container' (default) or 'local'."));
                break;
        }

        var allowLocal = doc.AllowLocalExecution ?? d.AllowLocalExecution;
        if (runtime == AgentRuntime.Local && !allowLocal)
        {
            errors.Add(new(ConfigErrorCodes.LocalAgentNotAllowed, "agent.runtime", "'local' requires agent.allowLocalExecution: true (ADR-0004); there is no automatic fallback to the host."));
        }

        return new AgentSettings(
            runtime,
            allowLocal,
            doc.SendScreenshots ?? d.SendScreenshots,
            Int(doc.MaxCalls, d.MaxCalls, 0, 10_000, "agent.maxCalls", errors),
            Long(doc.MaxTokens, d.MaxTokens, 0, 100_000_000, "agent.maxTokens", errors),
            Decimal(doc.MaxCostUsd, d.MaxCostUsd, 0m, 1_000m, "agent.maxCostUsd", errors));
    }

    private static List<ProfileSettings> ValidateProfiles(List<ProfileDocument>? docs, AllowlistSettings allowlist, List<ConfigError> errors)
    {
        var result = new List<ProfileSettings>();
        if (docs is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < docs.Count; i++)
        {
            var path = $"profiles[{i}]";
            var doc = docs[i];
            if (doc is null)
            {
                errors.Add(new(ConfigErrorCodes.InvalidProfile, path, "Profile must be a mapping."));
                continue;
            }

            var before = errors.Count;
            var profileId = doc.ProfileId?.Trim();
            if (string.IsNullOrEmpty(profileId) || !IdentifierPattern().IsMatch(profileId))
            {
                errors.Add(new(ConfigErrorCodes.InvalidProfile, $"{path}.profileId", "Required identifier (letters, digits, '_', '-')."));
            }
            else if (!seen.Add(profileId))
            {
                errors.Add(new(ConfigErrorCodes.InvalidProfile, $"{path}.profileId", "Duplicate profileId."));
            }
            else if (allowlist.ProfileIds.Count > 0 && !allowlist.ProfileIds.Contains(profileId, StringComparer.Ordinal))
            {
                errors.Add(new(ConfigErrorCodes.ProfileOutsideAllowlist, $"{path}.profileId", "Profile is not in allowlist.profileIds."));
            }

            var platform = doc.Platform switch
            {
                "android" => Platform.Android,
                "ios" => Platform.Ios,
                _ => (Platform?)null,
            };
            if (platform is null)
            {
                errors.Add(new(ConfigErrorCodes.InvalidProfile, $"{path}.platform", "Must be 'android' or 'ios'."));
            }

            var appId = doc.AppId?.Trim();
            if (string.IsNullOrEmpty(appId) || !allowlist.AppIds.Contains(appId, StringComparer.Ordinal))
            {
                errors.Add(new(ConfigErrorCodes.ProfileOutsideAllowlist, $"{path}.appId", "Required and must be listed in allowlist.appIds."));
            }

            var environment = doc.Environment?.Trim();
            if (!string.Equals(environment, TestEnvironment, StringComparison.Ordinal) || !allowlist.Environments.Contains(environment, StringComparer.Ordinal))
            {
                errors.Add(new(ConfigErrorCodes.UnsupportedEnvironment, $"{path}.environment", $"Must be '{TestEnvironment}' and listed in allowlist.environments."));
            }

            var isolation = doc.DataIsolation switch
            {
                null or "shared-readonly" => DataIsolation.SharedReadOnly,
                "disposable" => DataIsolation.Disposable,
                _ => (DataIsolation?)null,
            };
            if (isolation is null)
            {
                errors.Add(new(ConfigErrorCodes.InvalidProfile, $"{path}.dataIsolation", "Must be 'shared-readonly' (default) or 'disposable'."));
            }

            var inputs = ValidateInputs(doc.Inputs, path, errors);
            var capabilities = (doc.Capabilities ?? []).Select(c => c?.Trim() ?? string.Empty).ToList();
            for (var c = 0; c < capabilities.Count; c++)
            {
                if (!IdentifierPattern().IsMatch(capabilities[c]))
                {
                    errors.Add(new(ConfigErrorCodes.InvalidProfile, $"{path}.capabilities[{c}]", "Capability must be an identifier."));
                }
            }

            if (errors.Count == before)
            {
                result.Add(new ProfileSettings(profileId!, platform!.Value, appId!, environment!, isolation!.Value, inputs, capabilities));
            }
        }

        return result;
    }

    private static List<ProfileInput> ValidateInputs(Dictionary<string, string>? inputs, string profilePath, List<ConfigError> errors)
    {
        var result = new List<ProfileInput>();
        if (inputs is null)
        {
            return result;
        }

        foreach (var (name, value) in inputs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var path = $"{profilePath}.inputs.{name}";
            if (!IdentifierPattern().IsMatch(name))
            {
                errors.Add(new(ConfigErrorCodes.InvalidProfile, $"{profilePath}.inputs", "Input names must be identifiers."));
                continue;
            }

            if (SecretRef.TryParse(value, out var secret))
            {
                result.Add(new ProfileInput(name, secret, null));
            }
            else if (SecretRef.IsSecretUri(value))
            {
                errors.Add(new(ConfigErrorCodes.InvalidSecretRef, path, "Secret references must have the form secret://env/NAME (NAME: A-Z, 0-9, '_'). Other secret providers are not supported."));
            }
            else if (value is not null && value.StartsWith(GeneratorPrefix, StringComparison.Ordinal) && GeneratorPattern().IsMatch(value[GeneratorPrefix.Length..]))
            {
                result.Add(new ProfileInput(name, null, value[GeneratorPrefix.Length..]));
            }
            else
            {
                // Deliberately does not echo the value: it is most likely a pasted secret.
                errors.Add(new(ConfigErrorCodes.LiteralInputValue, path, "Literal input values are not allowed; use secret://env/NAME or generated:<kind>. The value is not shown."));
            }
        }

        return result;
    }

    private static int Int(int? value, int fallback, int min, int max, string path, List<ConfigError> errors)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value < min || value > max)
        {
            errors.Add(new(ConfigErrorCodes.BudgetOutOfRange, path, $"Must be between {min} and {max}."));
            return fallback;
        }

        return value.Value;
    }

    private static long Long(long? value, long fallback, long min, long max, string path, List<ConfigError> errors)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value < min || value > max)
        {
            errors.Add(new(ConfigErrorCodes.BudgetOutOfRange, path, $"Must be between {min} and {max}."));
            return fallback;
        }

        return value.Value;
    }

    private static decimal Decimal(decimal? value, decimal fallback, decimal min, decimal max, string path, List<ConfigError> errors)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value < min || value > max)
        {
            errors.Add(new(ConfigErrorCodes.BudgetOutOfRange, path, $"Must be between {min} and {max}."));
            return fallback;
        }

        return value.Value;
    }

    private static TimeSpan Duration(string? value, TimeSpan fallback, TimeSpan min, TimeSpan max, string path, List<ConfigError> errors)
    {
        if (value is null)
        {
            return fallback;
        }

        if (!DurationParser.TryParse(value.Trim(), out var parsed))
        {
            errors.Add(new(ConfigErrorCodes.InvalidDuration, path, "Duration must look like 500ms, 30s, 60m or 1h."));
            return fallback;
        }

        if (parsed < min || parsed > max)
        {
            errors.Add(new(ConfigErrorCodes.BudgetOutOfRange, path, $"Must be between {min} and {max}."));
            return fallback;
        }

        return parsed.Value;
    }

    [GeneratedRegex("^[A-Za-z0-9._*-]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowlistEntryPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratorPattern();
}
