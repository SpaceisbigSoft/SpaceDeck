using SpaceDeck.Collector.Polling;
using SpaceDeck.Core.Replay;

namespace SpaceDeck.Collector.Tests;

public class ReplayModeTests
{
    [Fact]
    public void Replay_file_replaces_the_hardware_sources()
    {
        var sources = new DefaultSensorSourceFactory().Create(new CollectorOptions { ReplayFile = @"C:\demo\laptop.sdrec" });
        var source = Assert.Single(sources);
        Assert.IsType<ReplaySource>(source);
        Assert.Equal("replay", source.Name);
    }

    [Fact]
    public void Replay_mode_is_only_on_with_a_file()
    {
        Assert.False(new CollectorOptions().IsReplay);
        Assert.False(new CollectorOptions { ReplayFile = " " }.IsReplay);
        Assert.True(new CollectorOptions { ReplayFile = "x.sdrec" }.IsReplay);
    }
}
