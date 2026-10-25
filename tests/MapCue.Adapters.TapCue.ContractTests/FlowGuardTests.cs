using MapCue.Adapters.TapCue.Flows;

namespace MapCue.Adapters.TapCue.ContractTests;

/// <summary>Forbidden commands and expressions (docs/tapcue-compatibility.md §2.3).</summary>
public class FlowGuardTests
{
    public static TheoryData<string> ForbiddenCommands() => new(FlowGuard.ForbiddenCommands.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(ForbiddenCommands))]
    public void Forbidden_command_is_rejected_as_mapping_and_as_scalar(string command)
    {
        Assert.Contains($"command '{command}' is forbidden", FlowGuard.Check($"appId: a\n---\n- {command}: x\n"));
        Assert.Contains($"command '{command}' is forbidden", FlowGuard.Check($"appId: a\n---\n- {command}\n"));
    }

    [Fact]
    public void Forbidden_commands_are_found_in_nested_blocks()
    {
        const string flow = "appId: a\n---\n- repeat:\n    times: 2\n    commands:\n      - retry:\n          commands:\n            - runScript: s.js\n";
        Assert.Contains("command 'runScript' is forbidden", FlowGuard.Check(flow));
    }

    [Theory]
    [InlineData("appId: a\n---\n- inputText: ${output.secret}\n")]
    [InlineData("appId: a\n---\n- tapOn: \"${1+1}\"\n")]
    [InlineData("appId: a\n---\n- assertVisible:\n    text: \"x ${maestro.copiedText}\"\n")]
    [InlineData("appId: a\nenv:\n  X: ${http.get('http://10.0.0.1').body}\n---\n- back\n")]
    public void Javascript_expressions_are_rejected(string flow)
    {
        Assert.Contains(FlowGuard.Check(flow), p => p.StartsWith("JavaScript expressions", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("appId: a\nonFlowStart:\n  - runScript: x\n---\n- back\n", "flow config key 'onFlowStart' is not allowed")]
    [InlineData("appId: a\njsEngine: graaljs\n---\n- back\n", "flow config key 'jsEngine' is not allowed")]
    [InlineData("url: https://example.com\n---\n- back\n", "flow config key 'url' is not allowed")]
    [InlineData("appId: a\n---\nback: true\n", "flow commands must be a sequence")]
    [InlineData("appId: a\n---\n- back\n---\n- back\n", "flow must have an optional config document and one command document")]
    [InlineData("appId: [\n", "flow is not valid YAML (line 2, column 1)")]
    public void Structure_is_checked(string flow, string problem)
    {
        Assert.Contains(problem, FlowGuard.Check(flow));
    }

    [Fact]
    public void Plain_env_references_and_argument_values_are_allowed()
    {
        const string flow = "appId: ${APP_ID}\nname: replay\ntags: [smoke]\nenv:\n  APP_ID: com.android.settings\n---\n- launchApp\n- tapOn:\n    text: \"runScript\"\n- inputText: \"${MAPCUE_IN_1}\"\n- takeScreenshot: launched\n";
        Assert.Empty(FlowGuard.Check(flow));
        Assert.Equal("com.android.settings", FlowGuard.ResolveAppId(flow));
    }

    [Fact]
    public void Spike_flows_pass_the_guard()
    {
        // The STEP-00 smoke flows are the reference for what MapCue replays.
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "spikes", "tapcue-smoke");
        foreach (var name in new[] { "single-action.yaml", "settings-replay.yaml" })
        {
            Assert.Empty(FlowGuard.Check(File.ReadAllText(Path.Combine(root, name))));
        }
    }

    [Theory]
    [InlineData("---\n- back\n", null)]
    [InlineData("- back\n", null)]
    [InlineData("appId: com.a\n---\n- back\n", "com.a")]
    [InlineData("appId: ${APP_ID}\n---\n- back\n", null)]
    [InlineData("appId: ${APP_ID}\nenv:\n  APP_ID: ${OTHER}\n---\n- back\n", null)]
    public void App_id_resolution(string flow, string? expected)
    {
        Assert.Equal(expected, FlowGuard.ResolveAppId(flow));
    }

    [Fact]
    public void Oversized_flows_are_rejected()
    {
        Assert.Contains($"flow exceeds {FlowGuard.MaxFlowBytes} bytes", FlowGuard.Check("appId: a\n---\n- back\n" + new string('#', FlowGuard.MaxFlowBytes)));
    }
}
