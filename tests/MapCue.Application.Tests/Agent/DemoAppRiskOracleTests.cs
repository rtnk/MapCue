using MapCue.Application.Agent;
using MapCue.Domain;

namespace MapCue.Application.Tests.Agent;

/// <summary>
/// The demo app's oracle (<c>samples/MapCue.DemoApp/expected-graph.md</c>, table "Ryzyko akcji") states the risk class MapCue's
/// rules give every action of the app, seen as on Android (resource id from the AutomationId, label from the text).
/// This keeps the oracle and <see cref="RiskClassifier"/> from drifting apart (STEP-02B).
/// </summary>
public class DemoAppRiskOracleTests
{
    private const string Begin = "<!-- risk-oracle:begin -->";
    private const string End = "<!-- risk-oracle:end -->";

    public sealed record OracleRow(string Role, string? AutomationId, string? Text, RiskClass Risk)
    {
        public override string ToString() => $"{Role} '{AutomationId ?? Text}'";
    }

    private static string OraclePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MapCue.sln")))
            {
                dir = dir.Parent;
            }

            return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("MapCue.sln not found."), "samples", "MapCue.DemoApp", "expected-graph.md");
        }
    }

    private static List<OracleRow> ReadRows()
    {
        var text = File.ReadAllText(OraclePath);
        var start = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "risk oracle markers missing in expected-graph.md");

        return text[(start + Begin.Length)..end]
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith('|') && !line.StartsWith("|---", StringComparison.Ordinal) && !line.StartsWith("| Rola", StringComparison.Ordinal))
            .Select(line =>
            {
                var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                Assert.Equal(4, cells.Length);
                return new OracleRow(
                    cells[0],
                    cells[1].Length == 0 ? null : cells[1],
                    cells[2].Length == 0 ? null : cells[2],
                    Enum.Parse<RiskClass>(cells[3], ignoreCase: true));
            })
            .ToList();
    }

    public static TheoryData<OracleRow> Rows() => new(ReadRows());

    [Fact]
    public void Oracle_covers_every_risk_class_the_demo_app_is_built_to_exercise()
    {
        var risks = ReadRows().Select(r => r.Risk).ToHashSet();
        RiskClass[] expected = [RiskClass.Navigational, RiskClass.StateChanging, RiskClass.Destructive, RiskClass.Financial, RiskClass.Communication, RiskClass.AccountChange];
        Assert.All(expected, r => Assert.Contains(r, risks));
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Rule_classifier_matches_the_oracle(OracleRow row)
    {
        // On Android the AutomationId is the resource id (expected-graph.md, "Jak MapCue widzi aplikację"); the label is the text.
        var resourceId = row.AutomationId is null ? null : "com.mapcue.demoapp:id/" + row.AutomationId;
        var assessment = new RiskClassifier().Classify(ActionType.Tap, row.Role, resourceId, [row.Text]);
        Assert.Equal(row.Risk, assessment.Risk);
    }
}
