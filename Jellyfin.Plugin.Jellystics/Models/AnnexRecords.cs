namespace Jellyfin.Plugin.Jellystics.Models;

/// <summary>Item added to / removed from the library.</summary>
public sealed class LibraryEventRecord
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = "added";   // added (live) | present (first-run snapshot) | removed
    public DateTimeOffset? DateCreated { get; set; }   // Jellyfin item DateCreated (UTC), on added/present rows
    public DateTimeOffset At { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public string? ItemType { get; set; }
    public string? ItemName { get; set; }
    public string? SeriesId { get; set; }
    public string? AlbumId { get; set; }
    public string? LibraryId { get; set; }
    public long? RunTimeTicks { get; set; }
    public long? SizeBytes { get; set; }
    public long Version { get; set; }
}

/// <summary>Played / unplayed / favorite / unfavorite mark.</summary>
public sealed class UserDataEventRecord
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;   // played | unplayed | favorite | unfavorite
    public string? Reason { get; set; }
    public long Version { get; set; }
}

/// <summary>Authentication attempt.</summary>
public sealed class LoginRecord
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? Client { get; set; }
    public string? ApplicationVersion { get; set; }
    public string? RemoteEndPoint { get; set; }
    public bool Success { get; set; }
    public long Version { get; set; }
}
