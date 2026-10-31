using MapCue.Domain;
using MapCue.Domain.Secrets;

namespace MapCue.Application.Configuration;

/// <summary>Validated, immutable configuration. Only <see cref="MapCueConfigValidator"/> creates it from a document.</summary>
public sealed record MapCueSettings(
    string Workspace,
    AllowlistSettings Allowlist,
    Budgets Budgets,
    AgentSettings Agent,
    IReadOnlyList<ProfileSettings> Profiles,
    TapCueSettings TapCue)
{
    public const string DefaultWorkspace = ".mapcue";

    /// <summary>Operator policy rules (STEP-04). Defaults to no extra rules.</summary>
    public PolicySettings Policy { get; init; } = PolicySettings.Default;

    /// <summary>Screen identity settings (STEP-05). Defaults to no screen hints.</summary>
    public IdentitySettings Identity { get; init; } = IdentitySettings.Default;
}

/// <summary>
/// Screen identity settings (architecture §7.3 step 3): <see cref="ScreenHints"/> are patterns for the resource id of an element
/// that names the screen (e.g. the MAUI <c>AutomationId</c> of a page title). Compiled with a match timeout.
/// </summary>
public sealed record IdentitySettings(IReadOnlyList<System.Text.RegularExpressions.Regex> ScreenHints)
{
    public static IdentitySettings Default { get; } = new([]);

    /// <summary>
    /// Profile variant rule (STEP-07, R-04): with screen hints, a snapshot of another context that differs from a known state by at most
    /// this many identity tokens is a variant of it. 0 disables the rule.
    /// </summary>
    public int ProfileVariantMaxDelta { get; init; } = MapCue.Application.Graph.IdentityResolverOptions.DefaultProfileVariantMaxDelta;
}

/// <summary>What a run does with an action that would switch the app to another profile (an element of its <c>switchElements</c>).</summary>
public enum ProfileSwitchPolicy
{
    /// <summary>Default: such an action is an account change (never executed under shared-readonly data, approval otherwise).</summary>
    Deny,

    /// <summary>Such actions are classified like any other element (the context's graph may then mix profiles).</summary>
    Allow,
}

/// <summary>
/// Operator policy (architecture §9.3 step 4, §10.1): extra risk rules by resource id or label. Rules only raise the risk of
/// matching elements (e.g. <c>riskClass: destructive</c> denies them on shared profiles); they never lower it.
/// </summary>
public sealed record PolicySettings(IReadOnlyList<MapCue.Application.Agent.OperatorRiskRule> RiskRules)
{
    public static PolicySettings Default { get; } = new([]);

    /// <summary>Profile switches inside a run (STEP-07): denied by default.</summary>
    public ProfileSwitchPolicy ProfileSwitch { get; init; } = ProfileSwitchPolicy.Deny;
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

/// <summary>Which reasoner interprets states (ADR-0004). <see cref="RuleBased"/> needs no language model and is the default.</summary>
public enum ReasonerKind
{
    RuleBased,

    /// <summary><c>CliAgentReasoner</c> (agent CLI, container by default).</summary>
    Cli,
}

public enum AgentRuntime
{
    Container,
    Local,
}

/// <summary>
/// Reasoner settings (ADR-0004). Container runtime by default; local only with explicit opt-in. The CLI agent settings
/// (<see cref="Provider"/> … <see cref="ApiKey"/>) are used by <c>CliAgentReasoner</c>; <see cref="ContainerImage"/> has no default
/// on purpose (the operator chooses and pins the image).
/// </summary>
public sealed record AgentSettings(
    AgentRuntime Runtime,
    bool AllowLocalExecution,
    bool SendScreenshots,
    int MaxCalls,
    long MaxTokens,
    decimal MaxCostUsd)
{
    public const string ClaudeProvider = "claude";

    public static AgentSettings Default { get; } = new(
        AgentRuntime.Container,
        AllowLocalExecution: false,
        SendScreenshots: false,
        MaxCalls: 100,
        MaxTokens: 1_000_000,
        MaxCostUsd: 5m);

    /// <summary>Agent CLI provider. Only <c>claude</c> (<c>claude -p</c>) is implemented in STEP-04.</summary>
    public string Provider { get; init; } = ClaudeProvider;

    /// <summary>Agent CLI executable inside the container (or on the host for <c>local</c>); null = the provider's name.</summary>
    public string? Executable { get; init; }

    /// <summary>Container engine executable (<c>docker</c> or <c>podman</c>).</summary>
    public string ContainerEngine { get; init; } = "docker";

    /// <summary>Image with the agent CLI, preferably pinned by digest. Required for the container runtime.</summary>
    public string? ContainerImage { get; init; }

    /// <summary>Container network; null = the engine default. Egress restriction to the LLM API is the image/network's job (TOOL-007 later).</summary>
    public string? ContainerNetwork { get; init; }

    public string? Model { get; init; }

    /// <summary>Hard timeout of one agent call (the process tree is killed).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>Optional API key reference (<c>secret://env/NAME</c>), passed to the agent by name only, never on the command line.</summary>
    public MapCue.Domain.Secrets.SecretRef? ApiKey { get; init; }

    /// <summary>Reasoner used by <c>mapcue explore</c> (STEP-05); <c>rule-based</c> by default (offline, no cost).</summary>
    public ReasonerKind Reasoner { get; init; } = ReasonerKind.RuleBased;
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
    IReadOnlyList<string> Capabilities)
{
    /// <summary>
    /// Operator TapCue flow that brings the app from a clean start to the profile's start state (architecture §6), relative to the
    /// configuration file. Null: the engine starts the app with a plain <c>launchApp</c>.
    /// </summary>
    public string? SetupFlow { get; init; }

    /// <summary>Scenario of the profile's test data (part of the exploration context, e.g. <c>empty-accounts</c>).</summary>
    public string ScenarioId { get; init; } = DefaultScenario;

    /// <summary>Locale of the app under this profile (part of the exploration context); <c>und</c> when not set.</summary>
    public string Locale { get; init; } = DefaultLocale;

    /// <summary>
    /// Resource id patterns of the elements that switch the app to this profile (e.g. the profile picker button). In a run of another
    /// profile they are account changes unless <c>policy.profileSwitch</c> is <c>allow</c> (STEP-07).
    /// </summary>
    public IReadOnlyList<System.Text.RegularExpressions.Regex> SwitchElements { get; init; } = [];

    public const string DefaultScenario = "default";

    public const string DefaultLocale = "und";
}

/// <summary>How a full-fidelity observation (CLI <c>hierarchy</c>) coexists with the MCP session (ADR-0002, R-03).</summary>
public enum FullObservationMode
{
    /// <summary>Run <c>hierarchy --no-reinstall-driver</c> while the MCP session stays open.</summary>
    Shared,

    /// <summary>Close the MCP session for the duration of the CLI call; the next MCP call restarts the session.</summary>
    Exclusive,
}

/// <summary>
/// TapCue integration settings (ADR-0002, docs/tapcue-compatibility.md §8). The executable is a name looked up
/// on <c>PATH</c> or an explicit path; there are no built-in install locations.
/// </summary>
public sealed record TapCueSettings(
    string Executable,
    IReadOnlyList<string> SupportedVersions,
    bool AllowUntestedVersion,
    IReadOnlyList<string> PassEnvironment,
    TimeSpan StartupTimeout,
    FullObservationMode FullObservation,
    bool CheckGeneratedSyntax)
{
    public const string DefaultExecutable = "tapcue";

    public static TapCueSettings Default { get; } = new(
        DefaultExecutable,
        SupportedVersions: ["0.1.0"],
        AllowUntestedVersion: false,
        PassEnvironment: [],
        StartupTimeout: TimeSpan.FromSeconds(120),
        FullObservation: FullObservationMode.Shared,
        CheckGeneratedSyntax: true);
}
