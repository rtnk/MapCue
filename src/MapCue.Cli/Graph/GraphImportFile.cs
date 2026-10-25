using System.Text.Json;
using System.Text.Json.Nodes;
using MapCue.Adapters.TapCue.Parsing;
using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Cli.Graph;

/// <summary>The import plan file is missing, unreadable or invalid.</summary>
internal sealed class GraphImportFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Loader of <c>mapcue graph import</c> plans (<c>mapcue.graph-import/1</c>, JSON). Snapshot files are read relative to the plan:
/// <c>tapcue-inspect-screen</c> (MCP <c>inspect_screen</c> payload), <c>tapcue-hierarchy</c> (CLI <c>hierarchy</c> stdout),
/// <c>tapcue-mcp-transcript</c> (recorded JSONL transcript; <c>index</c> selects the n-th <c>inspect_screen</c> result) and
/// <c>mapcue-elements</c> (JSON array of raw elements). The plan never contains input values: secrets stay references.
/// </summary>
internal static class GraphImportFile
{
    public const string SchemaVersion = "mapcue.graph-import/1";

    public static GraphImportPlan Load(string path)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new GraphImportFileException("The plan is not a JSON object.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new GraphImportFileException($"Cannot read plan: {ex.Message}", ex);
        }

        if ((string?)root["schemaVersion"] != SchemaVersion)
        {
            throw new GraphImportFileException($"schemaVersion must be '{SchemaVersion}'.");
        }

        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var contextNode = root["context"] as JsonObject ?? throw new GraphImportFileException("'context' is required.");
        var platform = ParseEnum<Platform>(Required(contextNode, "platform"), "context.platform");
        ExplorationContext context;
        try
        {
            context = ExplorationContext.Create(
                Required(contextNode, "profileId"),
                platform,
                Required(contextNode, "appId"),
                Required(contextNode, "appBuildId"),
                (string?)contextNode["locale"] ?? "und",
                (string?)contextNode["scenarioId"] ?? "default",
                (contextNode["capabilities"] as JsonArray)?.Select(c => (string?)c ?? string.Empty));
        }
        catch (ArgumentException ex)
        {
            throw new GraphImportFileException($"Invalid context: {ex.Message}", ex);
        }

        var device = new DeviceId((string?)root["device"] ?? "import");
        var observations = new List<ImportObservation>();
        foreach (var node in root["observations"] as JsonArray ?? throw new GraphImportFileException("'observations' must be an array."))
        {
            var item = node as JsonObject ?? throw new GraphImportFileException("Every observation must be an object.");
            var id = Required(item, "id");
            var file = Path.GetFullPath(Path.Combine(baseDirectory, Required(item, "file")));
            var observedAt = item["observedAt"] is JsonValue at && DateTimeOffset.TryParse((string?)at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.UnixEpoch;
            observations.Add(new ImportObservation(id, ReadObservation(file, Required(item, "format"), item["index"] is JsonValue i ? (int)i : 0, device, observedAt, id)));
        }

        var transitions = new List<ImportTransition>();
        foreach (var node in root["transitions"] as JsonArray ?? [])
        {
            var item = node as JsonObject ?? throw new GraphImportFileException("Every transition must be an object.");
            var action = item["action"] as JsonObject ?? throw new GraphImportFileException("transition.action is required.");
            var type = ParseEnum<ActionType>(Required(action, "type"), "action.type");
            var selector = string.Join(
                ';',
                new[]
                {
                    (string?)action["resourceId"] is { Length: > 0 } rid ? "id:" + rid : null,
                    (string?)action["text"] is { Length: > 0 } text ? "text:" + text : null,
                    (string?)action["direction"] is { Length: > 0 } direction ? "dir:" + ParseEnum<ScrollDirection>(direction, "action.direction") : null,
                }.OfType<string>());
            var risk = (string?)action["risk"] is { } r ? ParseEnum<RiskClass>(r, "action.risk") : RiskClass.Navigational;
            var outcome = (string?)item["outcome"] is { } o ? ParseEnum<ActionOutcome>(o, "transition.outcome") : ActionOutcome.Succeeded;
            var durationMs = item["durationMs"] is JsonValue d ? (long)d : 0;
            transitions.Add(new ImportTransition(
                Required(item, "from"),
                new ActionSpec(type, selector, (string?)action["paramClass"], risk, CandidateSource.Rule),
                outcome,
                (string?)item["to"],
                TimeSpan.FromMilliseconds(durationMs)));
        }

        return new GraphImportPlan(context, observations, transitions);
    }

    private static RawObservation ReadObservation(string file, string format, int index, DeviceId device, DateTimeOffset observedAt, string id)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GraphImportFileException($"Observation '{id}': cannot read '{Path.GetFileName(file)}': {ex.Message}", ex);
        }

        try
        {
            switch (format)
            {
                case "tapcue-inspect-screen":
                    return new RawObservation(device, null, TapCueParsers.ParseInspectScreen(text), TapCueParsers.HashPayload(text), observedAt, ObservationFidelity.Fast, text);
                case "tapcue-mcp-transcript":
                    var payload = InspectScreenPayloads(text).ElementAtOrDefault(index)
                        ?? throw new GraphImportFileException($"Observation '{id}': transcript has no inspect_screen result #{index}.");
                    return new RawObservation(device, null, TapCueParsers.ParseInspectScreen(payload), TapCueParsers.HashPayload(payload), observedAt, ObservationFidelity.Fast, payload);
                case "tapcue-hierarchy":
                    var elements = TapCueParsers.ParseHierarchy(text, out var json);
                    return new RawObservation(device, null, elements, TapCueParsers.HashPayload(json), observedAt, ObservationFidelity.Full, json);
                case "mapcue-elements":
                    var raw = GraphJson.Deserialize<List<RawElement>>(text);
                    return new RawObservation(device, null, raw, ContentHash.Sha256(text), observedAt, ObservationFidelity.Fast, null);
                default:
                    throw new GraphImportFileException($"Observation '{id}': unknown format '{format}'.");
            }
        }
        catch (Exception ex) when (ex is TapCueFormatException or JsonException or InvalidOperationException)
        {
            throw new GraphImportFileException($"Observation '{id}': {ex.Message}", ex);
        }
    }

    /// <summary>Texts of <c>inspect_screen</c> results of a recorded MCP transcript, in order.</summary>
    private static List<string> InspectScreenPayloads(string transcript)
    {
        var requests = new Dictionary<string, string>(StringComparer.Ordinal);
        var payloads = new List<string>();
        foreach (var line in transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = JsonNode.Parse(line);
            var message = entry?["msg"];
            var id = message?["id"]?.ToJsonString();
            if (id is null)
            {
                continue;
            }

            if ((string?)entry!["dir"] == "c2s" && (string?)message!["method"] == "tools/call")
            {
                requests[id] = (string?)message["params"]?["name"] ?? string.Empty;
            }
            else if ((string?)entry["dir"] == "s2c" && requests.GetValueOrDefault(id) == "inspect_screen" && (string?)message!["result"]?["content"]?[0]?["text"] is { } text)
            {
                payloads.Add(text);
            }
        }

        return payloads;
    }

    private static string Required(JsonObject node, string name) =>
        (string?)node[name] is { Length: > 0 } value ? value : throw new GraphImportFileException($"'{name}' is required.");

    private static T ParseEnum<T>(string value, string field)
        where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new GraphImportFileException($"{field}: '{value}' is not one of {string.Join(", ", Enum.GetNames<T>())}.");
}
