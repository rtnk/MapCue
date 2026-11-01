using MapCue.Application.Agent;
using MapCue.Application.Configuration;
using MapCue.Application.ScreenContracts;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.Tests.ScreenContracts;

/// <summary>STEP-08: the <c>contracts</c> section, the keyboard probe action being engine-only, and keyboard targets and predicted order.</summary>
public class ContractConfigTests
{
    private static MapCueConfigDocument Document(ContractsDocument? contracts) => new()
    {
        SchemaVersion = "mapcue.config/1",
        Allowlist = new AllowlistDocument { AppIds = ["com.example.demo"], Environments = ["test"] },
        Profiles = [new ProfileDocument { ProfileId = "basic", Platform = "android", AppId = "com.example.demo", Environment = "test" }],
        Contracts = contracts,
    };

    [Fact]
    public void Contract_captures_are_off_by_default()
    {
        var settings = MapCueConfigValidator.Validate(Document(null)).Settings!;
        Assert.Equal(ContractSettings.Default, settings.Contracts);
        Assert.False(settings.Contracts.Enabled);
    }

    [Fact]
    public void The_contracts_section_is_read_and_its_press_limit_bounded()
    {
        var settings = MapCueConfigValidator.Validate(Document(new ContractsDocument { AnalyzeScreens = true, KeyboardFocus = true, MaxTabPresses = 20 })).Settings!;
        Assert.Equal(new ContractSettings(true, true, 20), settings.Contracts);

        var invalid = MapCueConfigValidator.Validate(Document(new ContractsDocument { MaxTabPresses = 0 }));
        Assert.Null(invalid.Settings);
        Assert.Contains(invalid.Errors, e => e.Path == "contracts.maxTabPresses");
        Assert.Contains(MapCueConfigValidator.Validate(Document(new ContractsDocument { MaxTabPresses = 51 })).Errors, e => e.Path == "contracts.maxTabPresses");
    }

    [Fact]
    public void A_reasoner_can_never_propose_a_key_press()
    {
        Assert.DoesNotContain(ActionType.PressKey, AgentPolicy.ProposableActionTypes);
        Assert.DoesNotContain(ActionType.PressKey, ActionTypes.DefaultAllowed);
        Assert.Equal([DeviceKeys.Tab], DeviceKeys.Allowed);
    }

    private static UiElement Element(int i, string key, bool interactive, string? bounds, UiElementStates flags = UiElementStates.Clickable | UiElementStates.Enabled, string role = "button") =>
        new("e" + i, key, ElementKeyStrength.ResourceId, role, key, key, key, flags, null, null, 0, interactive) { Bounds = bounds };

    private static NormalizedSnapshot Snapshot(params UiElement[] elements) =>
        new(3, Platform.Android, elements, [], [], "sha256:i", "sha256:l", "sha256:e", [], StateKind.Default, [], [], 0, 0);

    [Fact]
    public void Keyboard_targets_are_enabled_interactive_elements_without_scroll_only_containers()
    {
        var snapshot = Snapshot(
            Element(1, "list", true, "[0,0][100,100]", UiElementStates.Scrollable | UiElementStates.Enabled, "list"),
            Element(2, "ok", true, "[0,0][10,10]"),
            Element(3, "disabled", true, "[0,0][10,10]", UiElementStates.Clickable),
            Element(4, "title", false, "[0,0][10,10]", UiElementStates.Enabled, "text"),
            Element(5, "switch", true, null, UiElementStates.Enabled, "toggle"));
        Assert.Equal(["ok", "switch"], KeyboardTargets.Of(snapshot));
    }

    [Fact]
    public void The_predicted_order_reads_top_to_bottom_then_left_to_right_with_unplaced_elements_last()
    {
        var snapshot = Snapshot(
            Element(1, "tab.home", true, "[0,700][100,760]"),
            Element(2, "unplaced", true, null),
            Element(3, "right", true, "[200,100][300,150]"),
            Element(4, "left", true, "[0,100][100,150]"),
            Element(5, "top", true, "[0,10][300,60]"));
        Assert.Equal(["top", "left", "right", "tab.home", "unplaced"], ScreenContractBuilder.PredictedOrder(snapshot));
    }
}
