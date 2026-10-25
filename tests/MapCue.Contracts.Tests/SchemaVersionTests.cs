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
    public void Agent_contracts_carry_their_schema_version_by_default()
    {
        var request = new AgentExplorationRequest("req_1", "sha256:00", []);
        var response = new AgentExplorationResponse("req_1", "sha256:00", null, []);
        Assert.Equal("mapcue.agent-exploration-request/1", request.SchemaVersion);
        Assert.Equal("mapcue.agent-exploration-response/1", response.SchemaVersion);
    }

    [GeneratedRegex("^mapcue\\.[a-z][a-z-]*/[1-9][0-9]*$")]
    private static partial Regex VersionPattern();
}
