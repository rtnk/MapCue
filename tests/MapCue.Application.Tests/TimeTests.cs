using MapCue.Application.Configuration;
using MapCue.Application.Time;
using MapCue.Testing;

namespace MapCue.Application.Tests;

public class TimeTests
{
    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("5s", 5_000)]
    [InlineData("60m", 3_600_000)]
    [InlineData("1h", 3_600_000)]
    public void Durations_are_parsed(string text, long milliseconds)
    {
        Assert.True(DurationParser.TryParse(text, out var d));
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), d);
    }

    [Fact]
    public void Version7_ids_use_the_injected_clock_and_are_time_ordered()
    {
        var clock = new ManualClock();
        var ids = new Version7IdGenerator(clock);

        var first = ids.NewId("run");
        clock.Advance(TimeSpan.FromMilliseconds(5));
        var second = ids.NewId("run");

        Assert.StartsWith("run_", first, StringComparison.Ordinal);
        Assert.True(string.CompareOrdinal(first, second) < 0);
        var guid = Guid.ParseExact(first["run_".Length..], "N");
        Assert.Equal(7, guid.Version);
    }

    [Fact]
    public void Sequential_ids_are_deterministic_per_prefix()
    {
        var ids = new SequentialIdGenerator();
        Assert.Equal("task_0001", ids.NewId("task"));
        Assert.Equal("att_0001", ids.NewId("att"));
        Assert.Equal("task_0002", ids.NewId("task"));
    }
}
