using MapCue.Adapters.TapCue.Parsing;
using MapCue.Domain;

namespace MapCue.Adapters.TapCue.ContractTests;

/// <summary>
/// Parsing of device payloads. The Android fixtures are marked <c>.synthetic</c>: they follow the TapCue 0.1.0
/// formatter code (ViewHierarchyFormatters.kt, Jackson TreeNode) until a recording from the Android smoke replaces them.
/// </summary>
public class ParserContractTests
{
    private const string Android = "mcp-android-settings.synthetic.jsonl";

    [Fact]
    public void Device_list_maps_platform_type_and_connection()
    {
        var text = Fixtures.ToolText(Fixtures.Responses(Android, "tools/call", "list_devices").Single());
        var devices = TapCueParsers.ParseDevices(text);
        Assert.Equal(2, devices.Count);
        Assert.Equal(new DeviceId("emulator-5554"), devices[0].Id);
        Assert.Equal(Platform.Android, devices[0].Platform);
        Assert.True(devices[0].IsEmulator);
        Assert.True(devices[0].IsConnected);
        Assert.False(devices[1].IsConnected);
    }

    [Fact]
    public void Web_and_unknown_platforms_are_skipped()
    {
        var devices = TapCueParsers.ParseDevices("{\"devices\":[{\"device_id\":\"chromium\",\"platform\":\"web\",\"type\":\"real\",\"connected\":true},{\"device_id\":\"A\",\"platform\":\"ios\",\"type\":\"simulator\",\"connected\":true}]}");
        Assert.Equal([new DeviceId("A")], devices.Select(d => d.Id));
        Assert.Equal(Platform.Ios, devices[0].Platform);
    }

    [Fact]
    public void Inspect_screen_elements_are_flattened_in_pre_order_with_depth_and_defaults()
    {
        var text = Fixtures.ToolText(Fixtures.Responses(Android, "tools/call", "inspect_screen")[0]);
        var elements = TapCueParsers.ParseInspectScreen(text);

        Assert.Equal(16, elements.Count);
        Assert.Equal(0, elements[0].Depth);
        var search = elements.Single(e => e.AccessibilityText == "Search settings");
        Assert.True(search.Clickable);
        Assert.Equal("com.android.settings:id/search_action_bar", search.ResourceId);
        var list = elements.Single(e => e.ResourceId == "com.android.settings:id/recycler_view");
        Assert.True(list.Scrollable);
        Assert.True(list.Focused);
        Assert.True(list.Enabled);
        var title = elements.First(e => e.Text == "Network & internet");
        Assert.Equal(3, title.Depth);
        Assert.Equal("[189,440][600,500]", title.Bounds);
        Assert.Equal("android.widget.TextView", title.ClassName);
        Assert.False(title.Clickable);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"ui_schema\":{}}")]
    public void Malformed_inspect_screen_payloads_are_format_errors(string payload)
    {
        Assert.Throws<TapCueFormatException>(() => TapCueParsers.ParseInspectScreen(payload));
    }

    [Fact]
    public void Hierarchy_keeps_every_node_and_ignores_text_around_the_json()
    {
        var elements = TapCueParsers.ParseHierarchy(Fixtures.Text("hierarchy-android-settings.synthetic.txt"), out var json);

        Assert.StartsWith("{", json, StringComparison.Ordinal);
        Assert.Equal(6, elements.Count);
        Assert.Null(elements[0].ClassName);
        var list = elements.Single(e => e.ResourceId == "com.android.settings:id/recycler_view");
        Assert.True(list.Scrollable);
        Assert.True(list.Focused);
        Assert.Equal(2, list.Depth);
        Assert.Contains(elements, e => e.Text == "Network & internet" && e.Depth == 4);
        Assert.Contains(elements, e => e.Clickable && e.ClassName == "android.widget.LinearLayout");
    }

    [Fact]
    public void Hierarchy_without_json_is_a_format_error()
    {
        Assert.Throws<TapCueFormatException>(() => TapCueParsers.ParseHierarchy("No devices found\n", out _));
    }

    [Fact]
    public void Run_result_env_values_are_stripped_and_keys_kept()
    {
        const string echoed = "{\"success\":true,\"device_id\":\"emulator-5554\",\"commands_executed\":2,\"message\":\"Flow executed successfully\",\"env_vars\":{\"MAPCUE_IN_1\":\"hunter2-secret\",\"MAESTRO_CLOUD_TOKEN\":\"tok-123\"}}";
        var stripped = TapCueParsers.StripEnvValues(echoed);

        Assert.DoesNotContain("hunter2-secret", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-123", stripped, StringComparison.Ordinal);
        Assert.Contains("MAPCUE_IN_1", stripped, StringComparison.Ordinal);
        Assert.Equal((true, 2), TapCueParsers.ParseRunResult(stripped));
        Assert.Equal("plain error text", TapCueParsers.StripEnvValues("plain error text"));
    }

    [Fact]
    public void Payload_hash_is_stable()
    {
        Assert.Equal(TapCueParsers.HashPayload("{\"a\":1}"), TapCueParsers.HashPayload("{\"a\":1}"));
        Assert.NotEqual(TapCueParsers.HashPayload("{\"a\":1}"), TapCueParsers.HashPayload("{\"a\":2}"));
        Assert.StartsWith("sha256:", TapCueParsers.HashPayload(string.Empty), StringComparison.Ordinal);
    }
}
