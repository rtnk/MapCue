using System.Reflection;
using System.Text.RegularExpressions;
using MapCue.Contracts.Agent;

namespace MapCue.Contracts.Tests;

public partial class SchemaVersionTests
{
    public static TheoryData<string> AllVersions()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(SchemaVersions).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            data.Add((string)field.GetRawConstantValue()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllVersions))]
    public void Schema_versions_follow_the_naming_convention(string version)
    {
        Assert.Matches(VersionPattern(), version);
    }

    [Fact]
    public void Agent_contracts_carry_their_schema_version_first()
    {
        var response = new AgentExplorationResponse("req_1", "sha256:00", new AgentInterpretation(null, null, null, 0), [], [], [], []);
        Assert.Equal("mapcue.agent-exploration-response/1", response.SchemaVersion);
        Assert.StartsWith("{\"schemaVersion\":\"mapcue.agent-exploration-response/1\"", AgentJson.Serialize(response), StringComparison.Ordinal);
        Assert.Equal("mapcue.agent-exploration-request/1", AgentContractSamples.Request().SchemaVersion);
    }

    [GeneratedRegex("^mapcue\\.[a-z][a-z-]*/[1-9][0-9]*$")]
    private static partial Regex VersionPattern();
}
