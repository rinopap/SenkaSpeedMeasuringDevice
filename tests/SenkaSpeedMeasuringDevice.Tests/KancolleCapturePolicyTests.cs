using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class KancolleCapturePolicyTests
{
    [Fact]
    public void AcceptsTheFixedGamePage()
    {
        Assert.True(KancolleCapturePolicy.IsAllowedGamePage(new Uri(KancolleCapturePolicy.GamePageUrl)));
    }

    [Theory]
    [InlineData("http://play.games.dmm.com/game/kancolle")]
    [InlineData("https://play.games.dmm.com.evil.example/game/kancolle")]
    [InlineData("https://user@play.games.dmm.com/game/kancolle")]
    [InlineData("https://play.games.dmm.com:444/game/kancolle")]
    [InlineData("https://play.games.dmm.com/game/other")]
    [InlineData("https://play.games.dmm.com/game/kancolle/")]
    [InlineData("https://play.games.dmm.com/game/kancolle?next=https://evil.example")]
    [InlineData("https://play.games.dmm.com/game/kancolle#fragment")]
    public void RejectsNonCanonicalGamePages(string value)
    {
        Assert.False(KancolleCapturePolicy.IsAllowedGamePage(new Uri(value)));
    }

    [Fact]
    public void AcceptsOnlySelectedServerHttpsApi()
    {
        Assert.True(GameServerRegistry.TryGet(20, out var server));
        Assert.True(KancolleCapturePolicy.IsAllowedApi(
            new Uri("https://w20h.kancolle-server.com/kcsapi/api_port/port"), server));
    }

    [Theory]
    [InlineData("http://w20h.kancolle-server.com/kcsapi/api_port/port")]
    [InlineData("https://w01y.kancolle-server.com/kcsapi/api_port/port")]
    [InlineData("https://w20h.kancolle-server.com.evil.example/kcsapi/api_port/port")]
    [InlineData("https://w20h.kancolle-server.com:444/kcsapi/api_port/port")]
    [InlineData("https://w20h.kancolle-server.com/kcsapi/api_port/port/extra")]
    [InlineData("https://w20h.kancolle-server.com/kcsapi/api_get_member/port")]
    [InlineData("https://user@w20h.kancolle-server.com/kcsapi/api_port/port")]
    [InlineData("https://w20h.kancolle-server.com/kcsapi/api_port/port#fragment")]
    public void RejectsNonMatchingApiEndpoints(string value)
    {
        Assert.True(GameServerRegistry.TryGet(20, out var server));
        Assert.False(KancolleCapturePolicy.IsAllowedApi(new Uri(value), server));
    }
}
