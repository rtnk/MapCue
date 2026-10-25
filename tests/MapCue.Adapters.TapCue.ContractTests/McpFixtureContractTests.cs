using MapCue.Adapters.TapCue.Mcp;
using MapCue.Adapters.TapCue.Parsing;
using MapCue.Application.Ports;

namespace MapCue.Adapters.TapCue.ContractTests;

/// <summary>Pins the MCP surface of TapCue 0.1.0 as recorded from the real server (mcp-no-device.jsonl).</summary>
public class McpFixtureContractTests
{
    private const string NoDevice = "mcp-no-device.jsonl";

    [Fact]
    public void Initialize_reports_protocol_and_hardcoded_server_version()
    {
        var result = Fixtures.Responses(NoDevice, "initialize").Single()["result"]!;
        Assert.Equal(McpToolClient.ProtocolVersion, (string?)result["protocolVersion"]);
        Assert.Equal("tapcue", (string?)result["serverInfo"]!["name"]);
        // G-07: serverInfo.version is hard-coded and must not be used for compatibility decisions.
        Assert.Equal("1.0.0", (string?)result["serverInfo"]!["version"]);
        Assert.True((bool)result["capabilities"]!["tools"]!["listChanged"]!);
    }

    [Fact]
    public void Tools_list_contains_every_required_tool_and_input()
    {
        var response = Fixtures.Responses(NoDevice, "tools/list").Single();
        var tools = response["result"]!["tools"]!.AsArray()
            .Select(t => new McpToolInfo((string)t!["name"]!, t["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key).ToHashSet(StringComparer.Ordinal)))
            .ToList();

        Assert.Empty(McpToolClient.VerifyRequiredTools(tools));
        Assert.Equal(["cheat_sheet", "inspect_screen", "list_devices", "run", "take_screenshot"], tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["device_id", "dir", "env", "exclude_tags", "files", "include_tags", "yaml"], tools.Single(t => t.Name == "run").InputProperties.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Missing_required_tools_are_reported()
    {
        McpToolInfo[] tools = [new("list_devices", new HashSet<string>()), new("run", new HashSet<string> { "device_id" })];
        var problems = McpToolClient.VerifyRequiredTools(tools);
        Assert.Contains("missing tool 'inspect_screen'", problems);
        Assert.Contains("tool 'run' has no input 'yaml'", problems);
        Assert.Contains("tool 'run' has no input 'env'", problems);
    }

    [Fact]
    public void Empty_device_list_parses()
    {
        var text = Fixtures.ToolText(Fixtures.Responses(NoDevice, "tools/call", "list_devices").Single());
        Assert.Equal("{\"devices\":[]}", text);
        Assert.Empty(TapCueParsers.ParseDevices(text));
    }

    [Fact]
    public void Tool_failures_are_results_with_is_error_not_json_rpc_errors()
    {
        // G-08: every failure in the recording is a tools/call result with isError, never a JSON-RPC error object.
        var responses = Fixtures.Transcript(NoDevice).Where(e => e.Dir == "s2c").Select(e => e.Msg).ToList();
        Assert.DoesNotContain(responses, r => r.ContainsKey("error"));
        var failures = responses.Where(r => r["result"]?["isError"] is not null).Select(r => McpToolClient.ParseToolResult(r["result"])).ToList();
        Assert.Equal(5, failures.Count);
        Assert.All(failures, f => Assert.True(f.IsError));
    }


    [Fact]
    public void Recorded_error_texts_are_classified()
    {
        var texts = Fixtures.Transcript(NoDevice)
            .Where(e => e.Dir == "s2c" && e.Msg["result"]?["isError"] is not null)
            .Select(e => Fixtures.ToolText(e.Msg))
            .ToList();

        Assert.Equal(
            [
                (DeviceErrorKind.DeviceNotConnected, false),
                (DeviceErrorKind.InvalidRequest, false),
                (DeviceErrorKind.DeviceNotConnected, false),
                (DeviceErrorKind.InvalidRequest, false),
                (DeviceErrorKind.ToolNotFound, false),
            ],
            texts.Select(TapCueErrorClassifier.ClassifyDetailed));
        Assert.Equal("Failed to inspect screen: Device with id emulator-5554 is not connected", texts[0]);
        Assert.Equal("Failed to run flow: Device with id emulator-5554 is not connected", texts[2]);
    }
}
