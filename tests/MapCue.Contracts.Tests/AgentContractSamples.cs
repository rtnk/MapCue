using MapCue.Contracts.Agent;

namespace MapCue.Contracts.Tests;

internal static class AgentContractSamples
{
    public const string SnapshotHash = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    public const string RequestId = "req_0123456789abcdef01234567";

    public static AgentExplorationRequest Request() => new(
        RequestId,
        "mapcue.agent-prompt/1",
        new AgentTask("task_1", "att_1", "AnalyzeState"),
        new AgentSnapshot(
            SnapshotHash,
            "android",
            "default",
            [
                new AgentSnapshotElement("e1", "button", "Accounts", "app:id/tab_accounts", ["clickable", "enabled"], 2, true, false),
                new AgentSnapshotElement("e2", "text", "Saldo # zł", null, ["enabled"], 3, false, true),
            ],
            0),
        new AgentContext("basic", "pl-PL", ["investments"]),
        new AgentGraphHints([new AgentCandidateState("st_1", null, 0.72)], ["sha256:2222222222222222222222222222222222222222222222222222222222222222"]),
        new AgentPolicyView(["tap", "back"], ["safe", "navigational"], ["destructive"], ["profile.inputs.username"], ["email"]),
        new AgentLimits(20),
        null,
        "Interpret the screen.");

    /// <summary>A golden answer with placeholders replaced by valid values (refs become e1).</summary>
    public static string Golden(string file) =>
        System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "agent-responses", file))
                .Replace("{{requestId}}", RequestId, StringComparison.Ordinal)
                .Replace("{{snapshotHash}}", SnapshotHash, StringComparison.Ordinal),
            "\\{\\{ref:[^}]+\\}\\}",
            "e1");
}
