namespace MapCue.Domain;

/// <summary>Result class of a single device action (architecture §4).</summary>
public enum ActionOutcome
{
    Succeeded,
    Failed,
    TimedOut,

    /// <summary>The action may have been executed (timeout, killed process, broken STDIO). Never retried automatically for state-changing actions.</summary>
    OutcomeUnknown,
    Rejected,
}
