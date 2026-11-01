namespace MapCue.Contracts;

/// <summary>Versioned identifiers of MapCue contracts. Each JSON contract carries its own <c>schemaVersion</c>.</summary>
public static class SchemaVersions
{
    public const string Config = "mapcue.config/1";
    public const string AgentExplorationRequest = "mapcue.agent-exploration-request/1";
    public const string AgentExplorationResponse = "mapcue.agent-exploration-response/1";
    public const string ScreenContract = "mapcue.screen-contract/1";
}
