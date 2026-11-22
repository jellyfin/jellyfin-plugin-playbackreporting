using Jellyfin.Plugin.Jellystics.Models;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Jellystics.Storage;

/// <summary>Library events, user data events and logins. Every written row gets a new global Version.</summary>
public sealed class AnnexStore
{
    private readonly JellysticsDb _db;

    public AnnexStore(JellysticsDb db) => _db = db;

    // ---- library ----
    public void UpsertLibrary(IReadOnlyCollection<LibraryEventRecord> events) => Write(events, (c, tx, e) =>
    {
        // "added" events are unique per item while it stays in the library: a repeat burns no version; after a removal a new added event (timestamped id) is written.
        if (e.Kind == "added")
        {
            var last = LastKind(c, tx, e.ItemId);
            if (last == "removed") e.Id = $"add-{e.ItemId}-{e.At.UtcDateTime:yyyyMMddHHmmss}";
            else if (last is not null || Exists(c, tx, "LibraryEvents", e.Id)) return false;
        }
        e.Version = JellysticsDb.NextVersion(c, tx);
        Exec(c, tx,
            "INSERT INTO LibraryEvents (Id,Kind,At,ItemId,ItemType,ItemName,SeriesId,AlbumId,LibraryId,RunTimeTicks,SizeBytes,Version) " +
            "VALUES ($Id,$Kind,$At,$ItemId,$ItemType,$ItemName,$SeriesId,$AlbumId,$LibraryId,$RunTimeTicks,$SizeBytes,$Version) " +
            "ON CONFLICT(Id) DO UPDATE SET Kind=excluded.Kind,At=excluded.At,ItemId=excluded.ItemId,ItemType=excluded.ItemType,ItemName=excluded.ItemName," +
            "SeriesId=excluded.SeriesId,AlbumId=excluded.AlbumId,LibraryId=excluded.LibraryId,RunTimeTicks=excluded.RunTimeTicks,SizeBytes=excluded.SizeBytes,Version=excluded.Version",
            ("$Id", e.Id), ("$Kind", e.Kind), ("$At", JellysticsDb.Iso(e.At)), ("$ItemId", e.ItemId), ("$ItemType", e.ItemType), ("$ItemName", e.ItemName),
            ("$SeriesId", e.SeriesId), ("$AlbumId", e.AlbumId), ("$LibraryId", e.LibraryId), ("$RunTimeTicks", e.RunTimeTicks), ("$SizeBytes", e.SizeBytes), ("$Version", e.Version));
        return true;
    });

    public bool LibraryAddedExists(string itemId)
    {
        using var c = _db.Open();
        return Exists(c, null, "LibraryEvents", "add-" + itemId);
    }

    public long CountLibrary()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LibraryEvents";
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<LibraryEventRecord> LibrarySince(long since, int limit) => Read("LibraryEvents", since, limit, r => new LibraryEventRecord
    {
        Id = (string)r["Id"], Kind = (string)r["Kind"], At = JellysticsDb.ParseIso(r["At"])!.Value, ItemId = (string)r["ItemId"],
        ItemType = S(r, "ItemType"), ItemName = S(r, "ItemName"), SeriesId = S(r, "SeriesId"), AlbumId = S(r, "AlbumId"), LibraryId = S(r, "LibraryId"),
        RunTimeTicks = L(r, "RunTimeTicks"), SizeBytes = L(r, "SizeBytes"), Version = (long)r["Version"],
    });

    // ---- user data ----
    public void UpsertUserData(IReadOnlyCollection<UserDataEventRecord> events) => Write(events, (c, tx, e) =>
    {
        e.Version = JellysticsDb.NextVersion(c, tx);
        Exec(c, tx,
            "INSERT INTO UserDataEvents (Id,At,UserId,ItemId,Kind,Reason,Version) VALUES ($Id,$At,$UserId,$ItemId,$Kind,$Reason,$Version) " +
            "ON CONFLICT(Id) DO UPDATE SET At=excluded.At,UserId=excluded.UserId,ItemId=excluded.ItemId,Kind=excluded.Kind,Reason=excluded.Reason,Version=excluded.Version",
            ("$Id", e.Id), ("$At", JellysticsDb.Iso(e.At)), ("$UserId", e.UserId), ("$ItemId", e.ItemId), ("$Kind", e.Kind), ("$Reason", e.Reason), ("$Version", e.Version));
        return true;
    });

    public IReadOnlyList<UserDataEventRecord> UserDataSince(long since, int limit) => Read("UserDataEvents", since, limit, r => new UserDataEventRecord
    {
        Id = (string)r["Id"], At = JellysticsDb.ParseIso(r["At"])!.Value, UserId = (string)r["UserId"], ItemId = (string)r["ItemId"],
        Kind = (string)r["Kind"], Reason = S(r, "Reason"), Version = (long)r["Version"],
    });

    // ---- logins ----
    public void UpsertLogins(IReadOnlyCollection<LoginRecord> logins) => Write(logins, (c, tx, e) =>
    {
        e.Version = JellysticsDb.NextVersion(c, tx);
        Exec(c, tx,
            "INSERT INTO Logins (Id,At,UserId,UserName,DeviceId,DeviceName,Client,ApplicationVersion,RemoteEndPoint,Success,Version) " +
            "VALUES ($Id,$At,$UserId,$UserName,$DeviceId,$DeviceName,$Client,$ApplicationVersion,$RemoteEndPoint,$Success,$Version) " +
            "ON CONFLICT(Id) DO UPDATE SET At=excluded.At,UserId=excluded.UserId,UserName=excluded.UserName,DeviceId=excluded.DeviceId,DeviceName=excluded.DeviceName," +
            "Client=excluded.Client,ApplicationVersion=excluded.ApplicationVersion,RemoteEndPoint=excluded.RemoteEndPoint,Success=excluded.Success,Version=excluded.Version",
            ("$Id", e.Id), ("$At", JellysticsDb.Iso(e.At)), ("$UserId", e.UserId), ("$UserName", e.UserName), ("$DeviceId", e.DeviceId), ("$DeviceName", e.DeviceName),
            ("$Client", e.Client), ("$ApplicationVersion", e.ApplicationVersion), ("$RemoteEndPoint", e.RemoteEndPoint), ("$Success", e.Success ? 1 : 0), ("$Version", e.Version));
        return true;
    });

    public IReadOnlyList<LoginRecord> LoginsSince(long since, int limit) => Read("Logins", since, limit, r => new LoginRecord
    {
        Id = (string)r["Id"], At = JellysticsDb.ParseIso(r["At"])!.Value, UserId = S(r, "UserId"), UserName = S(r, "UserName"), DeviceId = S(r, "DeviceId"),
        DeviceName = S(r, "DeviceName"), Client = S(r, "Client"), ApplicationVersion = S(r, "ApplicationVersion"), RemoteEndPoint = S(r, "RemoteEndPoint"),
        Success = (long)r["Success"] != 0, Version = (long)r["Version"],
    });

    // ---- meta ----
    public string? GetMeta(string key)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Meta WHERE Key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO Meta (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ---- helpers ----
    private static string? LastKind(SqliteConnection c, SqliteTransaction? tx, string itemId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT Kind FROM LibraryEvents WHERE ItemId = $i ORDER BY Version DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$i", itemId);
        return cmd.ExecuteScalar() as string;
    }

    private void Write<T>(IReadOnlyCollection<T> rows, Func<SqliteConnection, SqliteTransaction, T, bool> write)
    {
        if (rows.Count == 0) return;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var row in rows) write(c, tx, row);
        tx.Commit();
    }

    private static bool Exists(SqliteConnection c, SqliteTransaction? tx, string table, string id)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT 1 FROM {table} WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private List<T> Read<T>(string table, long since, int limit, Func<SqliteDataReader, T> map)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} WHERE Version > $since ORDER BY Version LIMIT $limit";
        cmd.Parameters.AddWithValue("$since", since);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private static string? S(SqliteDataReader r, string col) => r[col] as string;
    private static long? L(SqliteDataReader r, string col) => r[col] is long v ? v : null;
}
