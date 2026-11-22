using Jellyfin.Plugin.Jellystics.Storage;

namespace Jellyfin.Plugin.Jellystics.Tests;

internal sealed class TempDb : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jellystics-{Guid.NewGuid():N}.db");
    public JellysticsDb Db { get; }
    public TempDb() { Db = new JellysticsDb(Path); Db.Migrate(); }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { Path, Path + "-wal", Path + "-shm" }) { if (File.Exists(f)) File.Delete(f); }
    }
}
