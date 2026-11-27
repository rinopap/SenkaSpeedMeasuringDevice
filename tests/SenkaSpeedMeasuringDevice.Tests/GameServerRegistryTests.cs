using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class GameServerRegistryTests
{
    [Fact]
    public void ContainsTwentyUniqueServersAndHosts()
    {
        Assert.Equal(20, GameServerRegistry.All.Count);
        Assert.Equal(20, GameServerRegistry.All.Select(server => server.Id).Distinct().Count());
        Assert.Equal(20, GameServerRegistry.All.Select(server => server.ApiHost).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(Enumerable.Range(1, 20), GameServerRegistry.All.Select(server => server.Id));
    }

    [Fact]
    public void ResolvesKnownIdsAndRejectsUnknownIds()
    {
        Assert.True(GameServerRegistry.TryGet(1, out var first));
        Assert.Equal("w01y.kancolle-server.com", first.ApiHost);
        Assert.False(GameServerRegistry.TryGet(0, out _));
        Assert.False(GameServerRegistry.TryGet(21, out _));
    }
}
