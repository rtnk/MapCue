namespace MapCue.Domain;

/// <summary>Typed device actions (architecture §10.1). Values outside <see cref="ActionTypes.DefaultAllowed"/> require explicit opt-in.</summary>
public enum ActionType
{
    Tap,
    Back,
    Scroll,
    Swipe,
    InputText,
    HideKeyboard,
    WaitForAnimationToEnd,
    LongPress,
    Point,
    OpenLink,
    SetPermissions,

    /// <summary>Launches an allowlisted app (lifecycle step of the engine and of reachability recipes; never chosen by the reasoner).</summary>
    LaunchApp,

    /// <summary>
    /// Presses one allowlisted key (<see cref="DeviceKeys"/>): the keyboard focus probe of <c>AuditAccessibility</c> (STEP-08, Android
    /// <c>Tab</c>). Engine only: never proposable by the reasoner and never part of an explored edge.
    /// </summary>
    PressKey,
}

/// <summary>Keys a <see cref="ActionType.PressKey"/> may press (TapCue <c>pressKey</c>; iOS has no <c>Tab</c>, G-04).</summary>
public static class DeviceKeys
{
    public const string Tab = "Tab";

    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(StringComparer.Ordinal) { Tab };
}

/// <summary>
/// Direction of a <see cref="ActionType.Scroll"/> (where new content is revealed: <see cref="Down"/> reveals content below)
/// or of a <see cref="ActionType.Swipe"/> (the direction the finger moves).
/// </summary>
public enum ScrollDirection
{
    Down,
    Up,
    Left,
    Right,
}

public static class ActionTypes
{
    /// <summary>Action types allowed without explicit profile approval.</summary>
    public static IReadOnlySet<ActionType> DefaultAllowed { get; } = new HashSet<ActionType>
    {
        ActionType.Tap,
        ActionType.Back,
        ActionType.Scroll,
        ActionType.Swipe,
        ActionType.InputText,
        ActionType.HideKeyboard,
        ActionType.WaitForAnimationToEnd,
    };
}
