using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapCue.Contracts.Agent;

/// <summary>
/// JSON settings of the agent contracts: camelCase, nulls written (the schemas require every property), unknown members
/// rejected, compact output. Used both to write requests and to read responses that already passed the schema.
/// </summary>
public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"JSON did not contain a {typeof(T).Name}.");

    public static T Deserialize<T>(JsonElement element) =>
        element.Deserialize<T>(Options) ?? throw new JsonException($"JSON did not contain a {typeof(T).Name}.");

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
