using System.Reflection;

namespace MapCue.Contracts.Schemas;

/// <summary>The JSON Schemas of <c>schemas/</c>, embedded in this assembly (source of truth of the contracts).</summary>
public static class ContractSchemas
{
    public const string AgentExplorationRequestFile = "agent-exploration-request.v1.schema.json";
    public const string AgentExplorationResponseFile = "agent-exploration-response.v1.schema.json";
    public const string ScreenContractFile = "screen-contract.v1.schema.json";

    private static readonly Lazy<JsonSchema> _request = new(() => JsonSchema.Parse(Text(AgentExplorationRequestFile)));
    private static readonly Lazy<JsonSchema> _response = new(() => JsonSchema.Parse(Text(AgentExplorationResponseFile)));

    private static readonly Lazy<JsonSchema> _screenContract = new(() => JsonSchema.Parse(Text(ScreenContractFile)));

    public static JsonSchema AgentExplorationRequest => _request.Value;

    public static JsonSchema ScreenContract => _screenContract.Value;

    public static JsonSchema AgentExplorationResponse => _response.Value;

    /// <summary>The schema file as embedded (e.g. to hand the response schema to an agent CLI).</summary>
    public static string Text(string fileName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MapCue.Schemas." + fileName)
            ?? throw new InvalidOperationException($"Schema '{fileName}' is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
