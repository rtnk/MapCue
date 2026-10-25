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
