using System.Reflection;
using MapCue.Application.Ports;

namespace MapCue.Adapters.TapCue.ContractTests;

public class AdapterAssemblyTests
{
    [Fact]
    public void Adapter_implements_the_device_port()
    {
        var adapter = typeof(TapCueDeviceAutomation).Assembly;
        Assert.Equal("STEP-02", adapter.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "MapCue.ImplementedIn").Value);
        Assert.True(typeof(IDeviceAutomation).IsAssignableFrom(typeof(TapCueDeviceAutomation)));
    }
}
