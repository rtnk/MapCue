using MapCue.Adapters.TapCue.Flows;
using MapCue.Application.Ports;
using MapCue.Domain;

namespace MapCue.Adapters.TapCue.ContractTests;

/// <summary>Golden tests of the single-action flow templates (architecture §10.1 layer 4).</summary>
public class FlowYamlGeneratorTests
{
    private const string App = "com.android.settings";
    private static readonly FlowYamlGenerator _generator = new([App], TimeSpan.FromSeconds(5));

    private static ValidatedDeviceAction Action(ActionType type, string? id = null, string? text = null, string? input = null, ScrollDirection? direction = null, string app = App, string actionId = "A1") =>
        new(actionId, app, type, RiskClass.Navigational, id, text, input, direction);

    [Fact]
    public void Tap_by_resource_id()
    {
        var flow = _generator.Generate(Action(ActionType.Tap, id: "com.android.settings:id/search_action_bar"));
        Assert.Equal(
            "appId: \"com.android.settings\"\n" +
            "name: \"mapcue-A1\"\n" +
            "---\n" +
            "- tapOn:\n" +
            "    id: \"com\\\\.android\\\\.settings:id/search_action_bar\"\n" +
            "    retryTapIfNoChange: false\n" +
            "- waitForAnimationToEnd:\n" +
            "    timeout: 5000\n",
            flow.Yaml);
        Assert.Equal("tapOn[id]+settle", flow.ShapeKey);
        Assert.Empty(flow.Environment);
    }

    [Fact]
    public void PressKey_tab_is_a_fixed_template_and_other_keys_are_refused()
    {
        var flow = _generator.Generate(Action(ActionType.PressKey) with { Key = DeviceKeys.Tab });
        Assert.Equal(
            "appId: \"com.android.settings\"\n" +
            "name: \"mapcue-A1\"\n" +
            "---\n" +
            "- pressKey: Tab\n" +
            "- waitForAnimationToEnd:\n" +
            "    timeout: 5000\n",
            flow.Yaml);
        Assert.Equal("pressKey:Tab+settle", flow.ShapeKey);
        Assert.Empty(FlowGuard.Check(flow.Yaml));
        Assert.Throws<FlowGenerationException>(() => _generator.Generate(Action(ActionType.PressKey)));
        Assert.Throws<FlowGenerationException>(() => _generator.Generate(Action(ActionType.PressKey) with { Key = "Power" }));
        Assert.Throws<FlowGenerationException>(() => _generator.Generate(Action(ActionType.PressKey) with { Key = "Tab\n- runScript: x" }));
    }

    [Fact]
    public void Text_selector_is_regex_escaped_and_yaml_quoted()
    {
        var flow = _generator.Generate(Action(ActionType.Tap, text: "Network & internet (5) \"x\" a.b*"));
        Assert.Contains("    text: \"Network & internet \\\\(5\\\\) \\\"x\\\" a\\\\.b\\\\*\"\n", flow.Yaml, StringComparison.Ordinal);
        Assert.Equal("tapOn[text]+settle", flow.ShapeKey);
    }

    [Fact]
    public void Screen_text_never_becomes_a_javascript_expression()
    {
        var flow = _generator.Generate(Action(ActionType.Tap, text: "${http.post('http://10.0.0.1')}\nrunScript: x"));
        Assert.DoesNotContain("${", flow.Yaml, StringComparison.Ordinal);
        Assert.Contains("\\\\$\\\\{http\\\\.post", flow.Yaml, StringComparison.Ordinal);
        Assert.Contains("\\nrunScript: x", flow.Yaml, StringComparison.Ordinal);
        Assert.Empty(FlowGuard.Check(flow.Yaml));
    }

    [Fact]
    public void Id_and_text_are_combined_when_both_are_given()
    {
        var flow = _generator.Generate(Action(ActionType.LongPress, id: "row", text: "Apps"));
        Assert.Contains("- longPressOn:\n    id: \"row\"\n    text: \"Apps\"\n- waitForAnimationToEnd", flow.Yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("retryTapIfNoChange", flow.Yaml, StringComparison.Ordinal);
        Assert.Equal("longPressOn[id][text]+settle", flow.ShapeKey);
    }

    [Theory]
    [InlineData(ActionType.Back, null, "- back\n- waitForAnimationToEnd:\n    timeout: 5000\n", "back+settle")]
    [InlineData(ActionType.LaunchApp, null, "- launchApp\n- waitForAnimationToEnd:\n    timeout: 5000\n", "launchApp+settle")]
    [InlineData(ActionType.HideKeyboard, null, "- hideKeyboard\n- waitForAnimationToEnd:\n    timeout: 5000\n", "hideKeyboard+settle")]
    [InlineData(ActionType.WaitForAnimationToEnd, null, "- waitForAnimationToEnd:\n    timeout: 5000\n", "waitForAnimationToEnd+settle")]
    [InlineData(ActionType.Scroll, null, "- scroll\n- waitForAnimationToEnd:\n    timeout: 5000\n", "scroll+settle")]
    [InlineData(ActionType.Scroll, ScrollDirection.Up, "- swipe:\n    direction: DOWN\n- waitForAnimationToEnd:\n    timeout: 5000\n", "swipe+settle")]
    [InlineData(ActionType.Swipe, ScrollDirection.Left, "- swipe:\n    direction: LEFT\n- waitForAnimationToEnd:\n    timeout: 5000\n", "swipe+settle")]
    public void Commands_without_selectors(ActionType type, ScrollDirection? direction, string body, string shape)
    {
        var flow = _generator.Generate(Action(type, direction: direction));
        Assert.EndsWith("---\n" + body, flow.Yaml, StringComparison.Ordinal);
        Assert.Equal(shape, flow.ShapeKey);
        Assert.Empty(FlowGuard.Check(flow.Yaml));
    }

    [Fact]
    public void Input_text_is_passed_through_env_and_never_written_into_the_flow()
    {
        var flow = _generator.Generate(Action(ActionType.InputText, input: "profile.inputs.password"), r => r == "profile.inputs.password" ? "S3cr3t!${x}" : null);
        Assert.EndsWith("---\n- inputText: \"${MAPCUE_IN_1}\"\n", flow.Yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("S3cr3t", flow.Yaml, StringComparison.Ordinal);
        Assert.Equal("S3cr3t!${x}", flow.Environment["MAPCUE_IN_1"]);
        Assert.Equal("inputText", flow.ShapeKey);
    }

    public static TheoryData<ValidatedDeviceAction> Rejected() =>
    [
        Action(ActionType.Tap),
        Action(ActionType.Tap, id: new string('x', 513)),
        Action(ActionType.Point, id: "x"),
        Action(ActionType.OpenLink, text: "https://example.com"),
        Action(ActionType.SetPermissions),
        Action(ActionType.Swipe),
        Action(ActionType.InputText),
        Action(ActionType.InputText, input: "profile.inputs.unknown"),
        Action(ActionType.Back, app: "com.evil.app"),
        Action(ActionType.Back, app: "com.android.settings\n---\n- runScript: x"),
        Action(ActionType.Back, actionId: "a b"),
        Action(ActionType.Back, actionId: "x\"\nappId: other"),
    ];

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Unsupported_or_unsafe_actions_are_rejected(ValidatedDeviceAction action)
    {
        Assert.Throws<FlowGenerationException>(() => _generator.Generate(action, _ => null));
    }

    [Fact]
    public void App_must_be_allowlisted_even_if_it_looks_valid()
    {
        var generator = new FlowYamlGenerator(["com.example.demo"], TimeSpan.FromSeconds(1));
        var ex = Assert.Throws<FlowGenerationException>(() => generator.Generate(Action(ActionType.Back)));
        Assert.Contains("allowlist", ex.Message, StringComparison.Ordinal);
    }
}
