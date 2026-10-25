using System.Text.Json.Nodes;

namespace MapCue.Adapters.TapCue.ContractTests;

/// <summary>Access to recorded TapCue fixtures (see Fixtures/README.md for provenance).</summary>
internal static class Fixtures
{
    public const string Version = "tapcue-0.1.0";

    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", Version, name);

    public static string Text(string name) => File.ReadAllText(PathOf(name));

    /// <summary>Entries of a JSONL transcript: direction (<c>c2s</c>/<c>s2c</c>) and message.</summary>
    public static IReadOnlyList<(string Dir, JsonObject Msg)> Transcript(string name) =>
        File.ReadLines(PathOf(name))
            .Where(l => l.Length > 0)
            .Select(l => JsonNode.Parse(l)!.AsObject())
            .Select(e => ((string)e["dir"]!, e["msg"]!.AsObject()))
            .ToList();

    /// <summary>The server response to the request with the given method (and tool name), by order of appearance.</summary>
    public static IReadOnlyList<JsonObject> Responses(string name, string method, string? tool = null)
    {
        var transcript = Transcript(name);
        var ids = transcript
            .Where(e => e.Dir == "c2s" && (string?)e.Msg["method"] == method && (tool is null || (string?)e.Msg["params"]?["name"] == tool) && e.Msg["id"] is not null)
            .Select(e => (long)e.Msg["id"]!)
            .ToList();
        return ids.Select(id => transcript.First(e => e.Dir == "s2c" && (long?)e.Msg["id"] == id).Msg).ToList();
    }

    /// <summary>The text content of a recorded <c>tools/call</c> response.</summary>
    public static string ToolText(JsonObject response) => (string)response["result"]!["content"]![0]!["text"]!;
}
