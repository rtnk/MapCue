using MapCue.Adapters.TapCue.Flows;

namespace MapCue.Adapters.TapCue.ContractTests;

/// <summary>
/// The TapCue flows of samples/MapCue.DemoApp (.tapcue/*.yaml) pass the flow guard MapCue applies before <c>tapcue test</c>, and every
/// one starts the app inside a <c>retry</c> block: the CI emulator sometimes does not reach the profile picker after a cold start with
/// <c>clearState</c> (Demo app run 41252379887, R-14). TapCue's own <c>check-syntax</c> runs on them in the Demo app workflow.
/// </summary>
public class DemoAppFlowTests
{
    private static string FlowDirectory => Path.Combine(AppContext.BaseDirectory, "DemoFlows");

    public static TheoryData<string> Flows()
    {
        var names = Directory.GetFiles(FlowDirectory, "*.yaml").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["setup-basic-empty.yaml", "setup-basic.yaml", "setup-investor.yaml", "tour-basic.yaml", "tour-investor.yaml"], names);
        return new(names!);
    }

    [Theory]
    [MemberData(nameof(Flows))]
    public void Demo_flow_passes_the_flow_guard(string name)
    {
        var yaml = File.ReadAllText(Path.Combine(FlowDirectory, name));

        Assert.Empty(FlowGuard.Check(yaml));
        Assert.Equal("com.mapcue.demoapp", FlowGuard.ResolveAppId(yaml));
    }

    [Theory]
    [MemberData(nameof(Flows))]
    public void Demo_flow_starts_the_app_inside_a_retry_block(string name)
    {
        var commands = File.ReadAllLines(Path.Combine(FlowDirectory, name))
            .SkipWhile(l => l.Trim() != "---").Skip(1)
            .Where(l => l.Length > 0 && !l.TrimStart().StartsWith('#'))
            .ToList();

        // The first command is the retry; it holds the clean launch and the wait for the profile picker, and nothing else launches the app.
        Assert.Equal("- retry:", commands[0]);
        Assert.Equal("    maxRetries: 2", commands[1]);
        var block = commands.Skip(2).TakeWhile(l => l.StartsWith("    ", StringComparison.Ordinal)).ToList();
        Assert.Contains("      - launchApp:", block);
        Assert.Contains("          clearState: true", block);
        Assert.Contains(block, l => l.Contains("screen.profile", StringComparison.Ordinal));
        Assert.Single(commands, l => l.TrimStart().StartsWith("- launchApp", StringComparison.Ordinal));
    }
}
