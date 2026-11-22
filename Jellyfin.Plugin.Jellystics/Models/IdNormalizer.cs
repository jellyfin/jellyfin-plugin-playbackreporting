namespace Jellyfin.Plugin.Jellystics.Models;

/// <summary>Jellyfin ids are stored as 32 hex chars without hyphens (Guid "N"); anything that is not a guid is left as is.</summary>
public static class IdNormalizer
{
    public static string? Normalize(string? id) => id is not null && Guid.TryParse(id, out var g) ? g.ToString("N") : id;

    public static void Normalize(PlayRecord p)
    {
        p.UserId = Normalize(p.UserId)!;
        p.ItemId = Normalize(p.ItemId)!;
        p.SeriesId = Normalize(p.SeriesId);
        p.SeasonId = Normalize(p.SeasonId);
        p.AlbumId = Normalize(p.AlbumId);
        p.LibraryId = Normalize(p.LibraryId);
        p.MediaSourceId = Normalize(p.MediaSourceId);
    }
}
