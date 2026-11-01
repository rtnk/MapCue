using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MapCue.Application.Ports;
using MapCue.Domain;

namespace MapCue.Adapters.TapCue.Parsing;

/// <summary>A tool payload could not be understood (format drift between TapCue versions, R-12).</summary>
public sealed class TapCueFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Parsers for TapCue 0.1.0 outputs (docs/tapcue-compatibility.md §2–§3). Pinned by contract tests on fixtures.</summary>
public static partial class TapCueParsers
{
    /// <summary>Parses <c>tapcue --version</c> output: first line <c>tapcue X.Y.Z</c>, followed by an attribution line.</summary>
    public static ToolVersion ParseVersion(string stdout)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        foreach (var line in stdout.Split('\n'))
        {
            var match = VersionLine().Match(line.Trim());
            if (match.Success)
            {
                return new ToolVersion(match.Groups[1].Value, match.Groups[2].Value);
            }
        }

        throw new TapCueFormatException("tapcue --version did not print 'tapcue <version>'.");
    }

    /// <summary>Parses the MCP <c>list_devices</c> payload. Unknown platforms (e.g. web) are skipped.</summary>
    public static IReadOnlyList<DeviceInfo> ParseDevices(string json)
    {
        var root = ParseObject(json, "list_devices");
        var result = new List<DeviceInfo>();
        foreach (var item in (root["devices"] as JsonArray) ?? throw new TapCueFormatException("list_devices payload has no 'devices' array."))
        {
            var id = (string?)item?["device_id"];
            var platform = ((string?)item?["platform"]) switch
            {
                "android" => Platform.Android,
                "ios" => Platform.Ios,
                _ => (Platform?)null,
            };
            if (string.IsNullOrWhiteSpace(id) || platform is null)
            {
                continue;
            }

            var type = (string?)item!["type"];
            var connected = item["connected"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            result.Add(new DeviceInfo(new DeviceId(id), platform.Value, (string?)item["name"], type is "emulator" or "simulator", connected));
        }

        return result;
    }

    /// <summary>Parses the compact <c>inspect_screen</c> JSON (<c>{ui_schema, elements}</c>) into pre-order elements.</summary>
    public static IReadOnlyList<RawElement> ParseInspectScreen(string json)
    {
        var root = ParseObject(json, "inspect_screen");
        var elements = root["elements"] as JsonArray ?? throw new TapCueFormatException("inspect_screen payload has no 'elements' array.");
        var result = new List<RawElement>();
        foreach (var node in elements)
        {
            AddCompact(node as JsonObject, 0, result);
        }

        return result;
    }

    /// <summary>
    /// Parses <c>tapcue hierarchy</c> output: a pretty-printed <c>TreeNode</c> JSON object. Text around the object
    /// (insights, banners) is ignored. Every node is kept (full fidelity), in pre-order.
    /// </summary>
    public static IReadOnlyList<RawElement> ParseHierarchy(string stdout, out string json)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        var start = stdout.IndexOf('{', StringComparison.Ordinal);
        var end = stdout.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new TapCueFormatException("tapcue hierarchy printed no JSON object.");
        }

        json = stdout[start..(end + 1)];
        var root = ParseObject(json, "hierarchy");
        var result = new List<RawElement>();
        AddTreeNode(root, 0, result);
        return result;
    }

    /// <summary>Content hash of an observation payload (<c>sha256:hex</c>). Not a screen identity (STEP-03).</summary>
    public static string HashPayload(string payload) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    /// <summary>
    /// Removes values of the <c>env_vars</c> object from a <c>run</c> result (G-06: TapCue echoes the flow env and
    /// every <c>MAESTRO_*</c> variable). Keys are kept for diagnostics. Non-JSON text is returned unchanged.
    /// </summary>
    public static string StripEnvValues(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return text;
        }

        if (node is not JsonObject obj)
        {
            return text;
        }

        if (obj["env_vars"] is JsonObject env)
        {
            obj["env_vars"] = new JsonArray(env.Select(kv => (JsonNode?)JsonValue.Create(kv.Key)).ToArray());
        }

        return obj.ToJsonString();
    }

    /// <summary>Reads <c>success</c> and <c>commands_executed</c> from an inline <c>run</c> result.</summary>
    public static (bool Success, int? CommandsExecuted) ParseRunResult(string json)
    {
        var root = ParseObject(json, "run");
        var success = root["success"] is JsonValue s && s.TryGetValue<bool>(out var ok) && ok;
        int? commands = root["commands_executed"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : null;
        return (success, commands);
    }

    private static JsonObject ParseObject(string json, string what)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? throw new TapCueFormatException($"{what} payload is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new TapCueFormatException($"{what} payload is not valid JSON.", ex);
        }
    }

    private static void AddCompact(JsonObject? node, int depth, List<RawElement> result)
    {
        if (node is null)
        {
            return;
        }

        result.Add(new RawElement(
            ResourceId: Str(node, "rid"),
            Text: Str(node, "txt"),
            AccessibilityText: Str(node, "a11y"),
            ClassName: Str(node, "cls"),
            Clickable: Flag(node, "clickable", false),
            Enabled: Flag(node, "enabled", true),
            Bounds: Str(node, "b"),
            HintText: Str(node, "hint"),
            Value: Str(node, "val"),
            Scrollable: Flag(node, "scroll", false),
            Checked: Flag(node, "checked", false),
            Focused: Flag(node, "focused", false),
            Selected: Flag(node, "selected", false),
            Depth: depth));
        foreach (var child in (node["c"] as JsonArray) ?? [])
        {
            AddCompact(child as JsonObject, depth + 1, result);
        }
    }

    private static void AddTreeNode(JsonObject node, int depth, List<RawElement> result)
    {
        var attributes = node["attributes"] as JsonObject ?? [];
        result.Add(new RawElement(
            ResourceId: Str(attributes, "resource-id"),
            Text: Str(attributes, "text"),
            AccessibilityText: Str(attributes, "accessibilityText") ?? Str(attributes, "content-desc"),
            ClassName: Str(attributes, "class"),
            Clickable: Flag(node, "clickable", false),
            Enabled: Flag(node, "enabled", true),
            Bounds: Str(attributes, "bounds"),
            HintText: Str(attributes, "hintText"),
            Value: Str(attributes, "value"),
            Scrollable: string.Equals(Str(attributes, "scrollable"), "true", StringComparison.Ordinal),
            Checked: Flag(node, "checked", false),
            Focused: Flag(node, "focused", false),
            Selected: Flag(node, "selected", false),
            Depth: depth,
            ImportantForAccessibility: Str(attributes, "important-for-accessibility") switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            }));
        foreach (var child in (node["children"] as JsonArray) ?? [])
        {
            if (child is JsonObject c)
            {
                AddTreeNode(c, depth + 1, result);
            }
        }
    }

    private static string? Str(JsonObject node, string key)
    {
        if (node[key] is not JsonValue value)
        {
            return null;
        }

        var text = value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString(),
            _ => null,
        };
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static bool Flag(JsonObject node, string key, bool fallback)
    {
        if (node[key] is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<bool>(out var b))
        {
            return b;
        }

        return value.TryGetValue<string>(out var s) ? bool.TryParse(s, out var parsed) ? parsed : fallback : fallback;
    }

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9-]*) ([0-9]+\.[0-9]+\.[0-9]+[A-Za-z0-9.+-]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionLine();
}
