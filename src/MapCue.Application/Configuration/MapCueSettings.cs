using MapCue.Domain;
using MapCue.Domain.Secrets;

namespace MapCue.Application.Configuration;

/// <summary>Validated, immutable configuration. Only <see cref="MapCueConfigValidator"/> creates it from a document.</summary>
public sealed record MapCueSettings(
    string Workspace,
    AllowlistSettings Allowlist,
    Budgets Budgets,
    AgentSettings Agent,
    IReadOnlyList<ProfileSettings> Profiles)
{
    public const string DefaultWorkspace = ".mapcue";
}

public sealed record AllowlistSettings(
    IReadOnlyList<string> AppIds,
    IReadOnlyList<string> Environments,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> ProfileIds);

/// <summary>Run budgets (architecture §10.2). <see cref="Default"/> holds the safe defaults.</summary>
public sealed record Budgets(
    int MaxDepth,
    int MaxScreens,
    int MaxStates,
    int MaxActions,
    TimeSpan MaxDuration,
    int MaxRetries,
    int MaxListRowsPerTemplate,
    TimeSpan UiSettleTimeout,
    TimeSpan ActionTimeout,
    TimeSpan ObserveTimeout)
{
    public static Budgets Default { get; } = new(
        MaxDepth: 8,
        MaxScreens: 40,
        MaxStates: 120,
        MaxActions: 300,
        MaxDuration: TimeSpan.FromMinutes(60),
        MaxRetries: 2,
        MaxListRowsPerTemplate: 2,
        UiSettleTimeout: TimeSpan.FromSeconds(5),
        ActionTimeout: TimeSpan.FromSeconds(30),
        ObserveTimeout: TimeSpan.FromSeconds(20));
}

public enum AgentRuntime
{
    Container,
    Local,
}

/// <summary>Reasoner settings (ADR-0004). Container runtime by default; local only with explicit opt-in.</summary>
public sealed record AgentSettings(
    AgentRuntime Runtime,
    bool AllowLocalExecution,
    bool SendScreenshots,
    int MaxCalls,
    long MaxTokens,
    decimal MaxCostUsd)
{
    public static AgentSettings Default { get; } = new(
        AgentRuntime.Container,
        AllowLocalExecution: false,
        SendScreenshots: false,
        MaxCalls: 100,
        MaxTokens: 1_000_000,
        MaxCostUsd: 5m);
}

/// <summary>An input value of a profile: either a secret reference or a deterministic generator.</summary>
public sealed record ProfileInput(string Name, SecretRef? Secret, string? Generator);

public sealed record ProfileSettings(
    string ProfileId,
    Platform Platform,
    string AppId,
    string Environment,
    DataIsolation DataIsolation,
    IReadOnlyList<ProfileInput> Inputs,
    IReadOnlyList<string> Capabilities);
