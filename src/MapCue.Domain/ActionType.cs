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
