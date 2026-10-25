using System.Reflection;
using MapCue.Application.Ports;

namespace MapCue.Adapters.TapCue.ContractTests;

/// <summary>Placeholder until STEP-02 adds fixture-based contract tests for the TapCue adapter.</summary>
public class SkeletonTests
{
    private static readonly Assembly _adapter = Assembly.Load("MapCue.Adapters.TapCue");

    [Fact]
    public void Adapter_is_a_skeleton_scheduled_for_step_02()
    {
        var planned = _adapter.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "MapCue.ImplementedIn");
        Assert.Equal("STEP-02", planned.Value);
        Assert.DoesNotContain(_adapter.GetTypes(), t => typeof(IDeviceAutomation).IsAssignableFrom(t));
    }
}
