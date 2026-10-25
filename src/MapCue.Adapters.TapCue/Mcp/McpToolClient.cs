using System.Text;
using System.Text.Json.Nodes;
using MapCue.Application.Ports;

namespace MapCue.Adapters.TapCue.Mcp;

/// <summary>A tool result. TapCue reports failures as <c>isError: true</c> with text, not as JSON-RPC errors (G-08).</summary>
public sealed record McpToolResult(bool IsError, string Text, IReadOnlyList<McpImage> Images);

public sealed record McpImage(string MimeType, string Base64Data);

/// <summary>A tool advertised by <c>tools/list</c> with the property names of its input schema.</summary>
public sealed record McpToolInfo(string Name, IReadOnlySet<string> InputProperties);

/// <summary>Server identity from the <c>initialize</c> handshake. TapCue hard-codes the version (G-07); it is informational only.</summary>
public sealed record McpServerInfo(string Name, string Version, string ProtocolVersion);

/// <summary>MCP client operations on top of <see cref="JsonRpcConnection"/>: handshake, tool discovery and tool calls.</summary>
internal sealed class McpToolClient(JsonRpcConnection connection)
{
    public const string ProtocolVersion = "2025-06-18";

    /// <summary>Tools and input properties MapCue depends on (docs/tapcue-compatibility.md §2.1).</summary>
    public static IReadOnlyDictionary<string, string[]> RequiredTools { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["list_devices"] = [],
        ["inspect_screen"] = ["device_id"],
        ["run"] = ["device_id", "yaml", "env"],
    };

    public async Task<McpServerInfo> InitializeAsync(string clientVersion, CancellationToken ct)
    {
        var parameters = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "mapcue", ["version"] = clientVersion },
        };
        var result = await connection.RequestAsync("initialize", parameters, ct).ConfigureAwait(false) as JsonObject
            ?? throw new TapCueException(DeviceErrorKind.InvalidRequest, "MCP initialize returned no result.");
        var protocol = (string?)result["protocolVersion"];
        if (string.IsNullOrEmpty(protocol))
        {
            throw new TapCueException(DeviceErrorKind.UnsupportedVersion, "MCP initialize result has no protocolVersion.");
        }

        var info = result["serverInfo"] as JsonObject;
        await connection.NotifyAsync("notifications/initialized", null, ct).ConfigureAwait(false);
        return new McpServerInfo((string?)info?["name"] ?? string.Empty, (string?)info?["version"] ?? string.Empty, protocol);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        var result = await connection.RequestAsync("tools/list", null, ct).ConfigureAwait(false);
        var tools = new List<McpToolInfo>();
        foreach (var tool in (result?["tools"] as JsonArray) ?? [])
        {
            var name = (string?)tool?["name"];
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var properties = (tool!["inputSchema"]?["properties"] as JsonObject)?.Select(p => p.Key).ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);
            tools.Add(new McpToolInfo(name, properties));
        }

        return tools;
    }

    /// <summary>Returns the problems (empty when every required tool and input property is present).</summary>
    public static IReadOnlyList<string> VerifyRequiredTools(IReadOnlyList<McpToolInfo> tools)
    {
        var problems = new List<string>();
        foreach (var (name, properties) in RequiredTools)
        {
            var tool = tools.FirstOrDefault(t => t.Name == name);
            if (tool is null)
            {
                problems.Add($"missing tool '{name}'");
                continue;
            }

            problems.AddRange(properties.Where(p => !tool.InputProperties.Contains(p)).Select(p => $"tool '{name}' has no input '{p}'"));
        }

        return problems;
    }

    public async Task<McpToolResult> CallToolAsync(string name, JsonObject arguments, CancellationToken ct)
    {
        var parameters = new JsonObject { ["name"] = name, ["arguments"] = arguments };
        var result = await connection.RequestAsync("tools/call", parameters, ct).ConfigureAwait(false);
        return ParseToolResult(result);
    }

    internal static McpToolResult ParseToolResult(JsonNode? result)
    {
        var text = new StringBuilder();
        var images = new List<McpImage>();
        foreach (var item in (result?["content"] as JsonArray) ?? [])
        {
            switch ((string?)item?["type"])
            {
                case "text":
                    text.Append((string?)item!["text"]);
                    break;
                case "image":
                    images.Add(new McpImage((string?)item!["mimeType"] ?? "application/octet-stream", (string?)item["data"] ?? string.Empty));
                    break;
            }
        }

        var isError = result?["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var b) && b;
        return new McpToolResult(isError, text.ToString(), images);
    }
}
