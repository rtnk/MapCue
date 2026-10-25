namespace MapCue.Domain;

/// <summary>Risk classes of an action (architecture §10.1). Ordered from least to most risky; <see cref="Unknown"/> is treated as <see cref="StateChanging"/>.</summary>
public enum RiskClass
{
    Safe,
    Navigational,
    StateChanging,
    Unknown,
    Destructive,
    Financial,
    Communication,
    AccountChange,
    External,
}

public static class RiskClasses
{
    /// <summary>Risk classes allowed by default.</summary>
    public static IReadOnlySet<RiskClass> DefaultAllowed { get; } = new HashSet<RiskClass>
    {
        RiskClass.Safe,
        RiskClass.Navigational,
    };
}
