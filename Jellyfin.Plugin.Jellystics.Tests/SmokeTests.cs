namespace Jellyfin.Plugin.Jellystics.Tests;

public class SmokeTests
{
    [Fact]
    public void PluginGuidMatchesBuildYaml()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "build.yaml"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var yaml = File.ReadAllText(Path.Combine(dir!, "build.yaml"));
        Assert.Contains($"guid: \"{Plugin.PluginGuid}\"", yaml);
    }
}
