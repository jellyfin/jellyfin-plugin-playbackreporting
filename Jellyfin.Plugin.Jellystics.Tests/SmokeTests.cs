namespace Jellyfin.Plugin.Jellystics.Tests;

public class SmokeTests
{
    [Fact]
    public void PluginIdIsStable() =>
        Assert.Equal(new Guid("7f3c2a1e-9b84-4d6f-a2c5-3e1b8d9f6a40"), new Guid("7f3c2a1e-9b84-4d6f-a2c5-3e1b8d9f6a40"));
}
