namespace MapCue.Domain.Graph;

/// <summary>Deduplication keys of frontier tasks (architecture §7.4, ADR-0006 §5). Inserted with <c>ON CONFLICT(task_key) DO NOTHING</c>.</summary>
public static class TaskKeys
{
    /// <summary><c>H(type, fromStateId, actionKey, contextSignature, scenarioId, appBuildId)</c>.</summary>
    public static string ExploreAction(string fromStateId, string actionKey, ExplorationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ContentHash.Of("mapcue.task/1", nameof(TaskType.ExploreAction), fromStateId, actionKey, context.Signature, context.ScenarioId, context.AppBuildId);
    }

    /// <summary><c>H(type, stateId, contextSignature, appBuildId, reasonerPromptVersion)</c>.</summary>
    public static string AnalyzeState(string stateId, ExplorationContext context, string reasonerPromptVersion)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ContentHash.Of("mapcue.task/1", nameof(TaskType.AnalyzeState), stateId, context.Signature, context.AppBuildId, reasonerPromptVersion);
    }

    /// <summary>Version of the screen capture tasks; a new version captures every state again.</summary>
    public const string ScreenCaptureVersion = "screen-capture/1";

    /// <summary><c>H(type, stateId, contextSignature, appBuildId, captureVersion)</c>: one full-hierarchy capture per state, context and build (STEP-08).</summary>
    public static string AnalyzeScreen(string stateId, ExplorationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ContentHash.Of("mapcue.task/1", nameof(TaskType.AnalyzeScreen), stateId, context.Signature, context.AppBuildId, ScreenCaptureVersion);
    }

    /// <summary><c>H(type, stateId, contextSignature, appBuildId, captureVersion)</c>: one keyboard focus probe per state, context and build (STEP-08).</summary>
    public static string AuditAccessibility(string stateId, ExplorationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ContentHash.Of("mapcue.task/1", nameof(TaskType.AuditAccessibility), stateId, context.Signature, context.AppBuildId, ScreenCaptureVersion);
    }
}
