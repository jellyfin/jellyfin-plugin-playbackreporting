using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Jellystics.Storage;

/// <summary>Connection factory + migrations for jellystics.db.</summary>
public sealed class JellysticsDb
{
    private readonly string _connectionString;

    public JellysticsDb(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public void Migrate()
    {
        using var c = Open();
        using (var create = c.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL); INSERT INTO schema_version SELECT 0 WHERE NOT EXISTS (SELECT 1 FROM schema_version);";
            create.ExecuteNonQuery();
        }
        long current;
        using (var get = c.CreateCommand()) { get.CommandText = "SELECT version FROM schema_version"; current = (long)get.ExecuteScalar()!; }
        for (var v = (int)current; v < Migrations.Scripts.Length; v++)
        {
            using var tx = c.BeginTransaction();
            using (var run = c.CreateCommand()) { run.Transaction = tx; run.CommandText = Migrations.Scripts[v]; run.ExecuteNonQuery(); }
            using (var set = c.CreateCommand()) { set.Transaction = tx; set.CommandText = "UPDATE schema_version SET version = $v"; set.Parameters.AddWithValue("$v", v + 1); set.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    /// <summary>Increments and returns the global version counter (inside the caller's transaction).</summary>
    public static long NextVersion(SqliteConnection c, SqliteTransaction tx)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE version_seq SET value = value + 1 WHERE id = 1; SELECT value FROM version_seq WHERE id = 1;";
        return (long)cmd.ExecuteScalar()!;
    }

    public static long CurrentVersion(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM version_seq WHERE id = 1";
        return (long)cmd.ExecuteScalar()!;
    }

    public static string? Iso(DateTimeOffset? v) => v?.UtcDateTime.ToString("O");
    public static DateTimeOffset? ParseIso(object v) => v is string s ? DateTimeOffset.ParseExact(s, "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind) : null;
}
