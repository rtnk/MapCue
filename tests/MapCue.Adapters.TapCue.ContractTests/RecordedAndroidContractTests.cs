using MapCue.Adapters.TapCue.Mcp;
using MapCue.Adapters.TapCue.Parsing;
using MapCue.Domain;

namespace MapCue.Adapters.TapCue.ContractTests;

/// <summary>
/// Real TapCue 0.1.0 output recorded on the Android emulator (API 34, GitHub Actions) by the STEP-02 smoke:
/// list_devices → run launchApp (Settings) → inspect_screen → run back → inspect_screen, and CLI hierarchy (launcher).
/// </summary>
public class RecordedAndroidContractTests
{
    private const string Recording = "mcp-android-settings.jsonl";

    [Fact]
    public void All_recorded_device_calls_succeeded()
    {
        var calls = Fixtures.Responses(Recording, "tools/call").Select(r => McpToolClient.ParseToolResult(r["result"])).ToList();
        Assert.Equal(5, calls.Count);
        Assert.All(calls, c => Assert.False(c.IsError));
    }

    [Fact]
    public void Device_list_reports_the_running_emulator_and_the_launchable_avd()
    {
        var devices = TapCueParsers.ParseDevices(Fixtures.ToolText(Fixtures.Responses(Recording, "tools/call", "list_devices").Single()));
        Assert.Equal([("emulator-5554", true), ("test", false)], devices.Select(d => (d.Id.Value, d.IsConnected)));
        Assert.All(devices, d => Assert.Equal(Platform.Android, d.Platform));
        Assert.All(devices, d => Assert.True(d.IsEmulator));
    }

    [Fact]
    public void Inline_run_result_has_no_env_vars_without_env_and_maestro_variables()
    {
        foreach (var response in Fixtures.Responses(Recording, "tools/call", "run"))
        {
            var text = Fixtures.ToolText(response);
            Assert.Equal("{\"success\":true,\"device_id\":\"emulator-5554\",\"commands_executed\":2,\"message\":\"Flow executed successfully\"}", text);
            Assert.Equal((true, 2), TapCueParsers.ParseRunResult(text));
        }
    }

    [Fact]
    public void Settings_screen_is_read_including_system_ui()
    {
        var screens = Fixtures.Responses(Recording, "tools/call", "inspect_screen").Select(r => TapCueParsers.ParseInspectScreen(Fixtures.ToolText(r))).ToList();

        var settings = screens[0];
        Assert.Equal(73, settings.Count);
        // System UI (status bar) is part of the snapshot; filtering it is normalization work (STEP-03).
        Assert.Contains(settings, e => e.ResourceId?.StartsWith("com.android.systemui:id/", StringComparison.Ordinal) == true);
        Assert.Contains(settings, e => e.Text == "Network & internet");
        Assert.Contains(settings, e => e.ResourceId == "com.android.settings:id/settings_homepage_container");
        Assert.Contains(settings, e => e.Clickable);

        var launcher = screens[1];
        Assert.Equal(58, launcher.Count);
        Assert.DoesNotContain(launcher, e => e.Text == "Network & internet");
        Assert.NotEqual(TapCueParsers.HashPayload(Fixtures.ToolText(Fixtures.Responses(Recording, "tools/call", "inspect_screen")[0])), TapCueParsers.HashPayload(Fixtures.ToolText(Fixtures.Responses(Recording, "tools/call", "inspect_screen")[1])));
    }

    [Fact]
    public void Full_hierarchy_keeps_attributes_the_compact_snapshot_drops()
    {
        var text = Fixtures.Text("hierarchy-android-launcher.txt");
        var elements = TapCueParsers.ParseHierarchy(text, out var json);

        Assert.Equal(59, elements.Count);
        var date = elements.Single(e => e.ResourceId == "com.google.android.apps.nexuslauncher:id/date");
        Assert.True(date.Clickable);
        Assert.Equal("[31,43][305,60]", date.Bounds);
        // G-02: lossless TreeNode attributes needed later for accessibility (STEP-08).
        Assert.Contains("\"important-for-accessibility\" : \"true\"", json, StringComparison.Ordinal);
    }
}
