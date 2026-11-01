using System.Text.Json;
using System.Text.Json.Nodes;
using MapCue.Contracts.Schemas;
using MapCue.Contracts.ScreenContracts;

namespace MapCue.Contracts.Tests;

/// <summary>
/// <c>schemas/screen-contract.v1.schema.json</c> (STEP-08): the schema loads in MapCue's validator, accepts a contract, and its typed cells make
/// a PASS from the tree (level <c>inspected</c>) impossible.
/// </summary>
public class ScreenContractSchemaTests
{
    private const string Hash = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    public static ScreenContractDocument Sample()
    {
        var notTested = new AssistiveCell("notTested", "manual protocol only", [], []);
        return new ScreenContractDocument(
            SchemaVersions.ScreenContract,
            "scr_1",
            "screen-1",
            null,
            1,
            Hash,
            new ContractGroup("com.app|Android|1|und", "com.app", "Android", "1", "und"),
            3,
            [new ContractState("st_1", "Default", Hash, ["ctx_1"])],
            [new ContractContext("ctx_1", "basic", "default", [])],
            [new ContractElement("id:com.app:id/ok", "ResourceId", "button", "observed", "OK", "com.app:id/ok", true, true, [new ContractPresence("ctx_1", "st_1", "full")], null)],
            [new ContractVariant("st_1", "ctx_1", Hash, ["i:id:com.app:id/ok"], [], [], "full", ["ev_1"])],
            new ContractAccessibility(
                [
                    new AccessibilityMatrixRow("TalkBack", new InspectedCell("present", "tree rules", 0, ["ev_1"]), new AutomatedCell("unsupported", "G-05", 0, []), notTested),
                    new AccessibilityMatrixRow("VoiceOver", new InspectedCell("unsupported", "android", 0, []), new AutomatedCell("unsupported", "android", 0, []), new AssistiveCell("unsupported", "android", [], [])),
                    new AccessibilityMatrixRow("Keyboard", new InspectedCell("unsupported", "no tree check", 0, []), new AutomatedCell("pass", "Tab", 0, ["ev_2"]), notTested),
                ],
                [],
                []),
            new ContractKeyboard(
                [new PredictedFocusOrder("ctx_1", "st_1", "predicted", "unverified", "tree order", ["id:com.app:id/ok"])],
                [new ObservedFocusGraph("ctx_1", "st_1", "automated", "pass", "Tab", [new FocusTransition(1, null, "id:com.app:id/ok")], ["id:com.app:id/ok"], [], true, true, null, ["ev_2"])]),
            ["ev_1", "ev_2"]);
    }

    private static IReadOnlyList<SchemaError> Validate(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return ContractSchemas.ScreenContract.Validate(document.RootElement);
    }

    private static JsonNode SampleNode() => JsonNode.Parse(ScreenContractJson.Serialize(Sample()))!;

    [Fact]
    public void A_contract_is_valid_and_carries_its_schema_version_and_cell_levels()
    {
        var node = SampleNode();
        Assert.Empty(Validate(node));
        Assert.Equal("mapcue.screen-contract/1", (string?)node["schemaVersion"]);
        Assert.Equal("inspected", (string?)node["accessibility"]!["matrix"]![0]!["inspected"]!["level"]);
        Assert.Equal("assistiveVerified", (string?)node["accessibility"]!["matrix"]![0]!["assistiveVerified"]!["level"]);
    }

    [Theory]
    [InlineData("inspected", "pass")]
    [InlineData("inspected", "verified")]
    [InlineData("assistiveVerified", "unverified")]
    [InlineData("automated", "present")]
    public void A_cell_status_outside_its_level_is_rejected(string cell, string status)
    {
        var node = SampleNode();
        node["accessibility"]!["matrix"]![0]![cell]!["status"] = status;
        Assert.NotEmpty(Validate(node));
    }

    [Fact]
    public void An_inspected_cell_cannot_claim_another_level()
    {
        var node = SampleNode();
        node["accessibility"]!["matrix"]![0]!["inspected"]!["level"] = "automated";
        node["accessibility"]!["matrix"]![0]!["inspected"]!["status"] = "pass";
        Assert.NotEmpty(Validate(node));
    }

    [Fact]
    public void A_predicted_order_is_always_unverified()
    {
        var node = SampleNode();
        node["keyboard"]!["predicted"]![0]!["status"] = "verified";
        Assert.NotEmpty(Validate(node));
    }

    [Fact]
    public void The_content_hash_ignores_version_and_hash_but_not_content()
    {
        var sample = Sample();
        var hash = ScreenContractJson.ContentHashOf(sample);
        Assert.Equal(hash, ScreenContractJson.ContentHashOf(sample with { Version = 7, ContentHash = "sha256:x" }));
        Assert.NotEqual(hash, ScreenContractJson.ContentHashOf(sample with { DisplayName = "Home" }));
        Assert.Matches("^sha256:[0-9a-f]{64}$", hash);
    }
}
