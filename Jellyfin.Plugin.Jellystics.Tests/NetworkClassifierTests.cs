using Jellyfin.Plugin.Jellystics.Collectors;

namespace Jellyfin.Plugin.Jellystics.Tests;

public class NetworkClassifierTests
{
    [Theory]
    [InlineData("192.168.1.20", false)]
    [InlineData("10.0.0.5:51234", false)]
    [InlineData("172.20.3.4", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("[fe80::1]:8096", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("::ffff:192.168.0.2", false)]
    [InlineData("ff02::1", false)]
    [InlineData("224.0.0.251", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("82.64.10.1", true)]
    [InlineData("2a01:e0a::1", true)]
    public void Classifies(string ep, bool remote) => Assert.Equal(remote, NetworkClassifier.IsRemote(ep));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    public void UnknownIsNull(string? ep) => Assert.Null(NetworkClassifier.IsRemote(ep));
}
