namespace MapCue.Domain.Graph;

/// <summary>
/// Exploration context (architecture §6): <c>signature = H(profileId, platform, appBuildId, locale, scenarioId, sorted(capabilities))</c>.
/// The context never enters a state identity; variants, edges, evidence and tasks are always per context (ADR-0006).
/// <see cref="ContextId"/> is derived from the signature, so the same context always gets the same id.
/// </summary>
public sealed record ExplorationContext
{
    private ExplorationContext(
        string contextId,
        string signature,
        string profileId,
        Platform platform,
        string appId,
        string appBuildId,
        string locale,
        string scenarioId,
        IReadOnlyList<string> capabilities)
    {
        ContextId = contextId;
        Signature = signature;
        ProfileId = profileId;
        Platform = platform;
        AppId = appId;
        AppBuildId = appBuildId;
        Locale = locale;
        ScenarioId = scenarioId;
        Capabilities = capabilities;
    }

    public string ContextId { get; }

    public string Signature { get; }

    public string ProfileId { get; }

    public Platform Platform { get; }

    public string AppId { get; }

    public string AppBuildId { get; }

    public string Locale { get; }

    public string ScenarioId { get; }

    /// <summary>Capabilities, sorted (ordinal) and distinct.</summary>
    public IReadOnlyList<string> Capabilities { get; }

    public static ExplorationContext Create(
        string profileId,
        Platform platform,
        string appId,
        string appBuildId,
        string locale = "und",
        string scenarioId = "default",
        IEnumerable<string>? capabilities = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appBuildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        var caps = (capabilities ?? []).Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        // appId is part of the signature as well: two apps never share a context even with the same build label.
        var signature = ContentHash.Of(["mapcue.context/1", profileId, platform.ToString(), appId, appBuildId, locale, scenarioId, .. caps]);
        return new ExplorationContext(ContentHash.ShortId("ctx", signature), signature, profileId, platform, appId, appBuildId, locale, scenarioId, caps);
    }

    public bool Equals(ExplorationContext? other) => other is not null && Signature == other.Signature;

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Signature);
}
