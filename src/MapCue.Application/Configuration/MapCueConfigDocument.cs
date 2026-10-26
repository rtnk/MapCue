namespace MapCue.Application.Configuration;

// Raw, mutable shape of mapcue.yaml as bound by the YAML loader. Nothing here is trusted until
// MapCueConfigValidator turns it into MapCueSettings. Unknown keys are rejected by the loader.
#pragma warning disable CA1002, CA2227 // Plain binding DTOs; validated into immutable settings.

public sealed class MapCueConfigDocument
{
    public string? SchemaVersion { get; set; }

    public string? Workspace { get; set; }

    public AllowlistDocument? Allowlist { get; set; }

    public BudgetsDocument? Budgets { get; set; }

    public AgentDocument? Agent { get; set; }

    public List<ProfileDocument>? Profiles { get; set; }

    /// <summary>Bound from the <c>tapcue</c> key (STEP-02).</summary>
    public TapcueDocument? Tapcue { get; set; }

    /// <summary>Operator policy rules (STEP-04).</summary>
    public PolicyDocument? Policy { get; set; }

    /// <summary>Screen identity hints (STEP-05).</summary>
    public IdentityDocument? Identity { get; set; }
}

public sealed class IdentityDocument
{
    public List<string>? ScreenHints { get; set; }
}

public sealed class AllowlistDocument
{
    public List<string>? AppIds { get; set; }

    public List<string>? Environments { get; set; }

    public List<string>? DeviceIds { get; set; }

    public List<string>? ProfileIds { get; set; }
}

public sealed class BudgetsDocument
{
    public int? MaxDepth { get; set; }

    public int? MaxScreens { get; set; }

    public int? MaxStates { get; set; }

    public int? MaxActions { get; set; }

    public string? MaxDuration { get; set; }

    public int? MaxRetries { get; set; }

    public int? MaxListRowsPerTemplate { get; set; }

    public string? UiSettleTimeout { get; set; }

    public string? ActionTimeout { get; set; }

    public string? ObserveTimeout { get; set; }
}

public sealed class AgentDocument
{
    public string? Runtime { get; set; }

    public bool? AllowLocalExecution { get; set; }

    public bool? SendScreenshots { get; set; }

    public int? MaxCalls { get; set; }

    public long? MaxTokens { get; set; }

    public decimal? MaxCostUsd { get; set; }

    public string? Provider { get; set; }

    public string? Executable { get; set; }

    public string? ContainerEngine { get; set; }

    public string? ContainerImage { get; set; }

    public string? ContainerNetwork { get; set; }

    public string? Model { get; set; }

    public string? Timeout { get; set; }

    public string? ApiKey { get; set; }

    public string? Reasoner { get; set; }
}

public sealed class PolicyDocument
{
    public List<RiskRuleDocument>? RiskRules { get; set; }
}

public sealed class RiskRuleDocument
{
    public string? Name { get; set; }

    public string? RiskClass { get; set; }

    public string? ResourceId { get; set; }

    public string? ResourceIdPattern { get; set; }

    public string? LabelPattern { get; set; }
}

public sealed class ProfileDocument
{
    public string? ProfileId { get; set; }

    public string? Platform { get; set; }

    public string? AppId { get; set; }

    public string? Environment { get; set; }

    public string? DataIsolation { get; set; }

    public Dictionary<string, string>? Inputs { get; set; }

    public List<string>? Capabilities { get; set; }

    public string? SetupFlow { get; set; }
}

public sealed class TapcueDocument
{
    public string? Executable { get; set; }

    public List<string>? SupportedVersions { get; set; }

    public bool? AllowUntestedVersion { get; set; }

    public List<string>? PassEnvironment { get; set; }

    public string? StartupTimeout { get; set; }

    public string? FullObservation { get; set; }

    public bool? CheckGeneratedSyntax { get; set; }
}

#pragma warning restore CA1002, CA2227
