using System.Reflection;

namespace Jellyfin.Plugin.Jellystics.Me;

public enum FallbackReason
{
    NotLinked,
    Unreachable,
}

/// <summary>Embedded client script and localized fallback page.</summary>
public static class MeResources
{
    private static readonly Lazy<byte[]> Client = new(() => ReadBytes("Me.client.js"));
    private static readonly Lazy<string> FallbackTemplate = new(() => System.Text.Encoding.UTF8.GetString(ReadBytes("Me.fallback.html")));

    private static readonly Dictionary<string, (string Title, string NotLinked, string Unreachable, string Back)> Texts = new()
    {
        ["fr"] = ("Statistiques indisponibles",
            "Jellystics n'est pas encore relié à ce serveur Jellyfin. Un administrateur doit configurer Jellystics et la synchronisation du plugin.",
            "Jellystics est injoignable depuis Jellyfin pour le moment. Réessayez dans quelques instants.",
            "Retour à Jellyfin"),
        ["en"] = ("Statistics unavailable",
            "Jellystics is not linked to this Jellyfin server yet. An administrator needs to set up Jellystics and the plugin sync.",
            "Jellystics cannot be reached from Jellyfin right now. Please try again in a moment.",
            "Back to Jellyfin"),
        ["es"] = ("Estadísticas no disponibles",
            "Jellystics todavía no está vinculado a este servidor Jellyfin. Un administrador debe configurar Jellystics y la sincronización del plugin.",
            "No se puede contactar con Jellystics desde Jellyfin en este momento. Inténtalo de nuevo en unos instantes.",
            "Volver a Jellyfin"),
    };

    public static byte[] ClientScript => Client.Value;

    /// <summary>fr, en or es from an Accept-Language header (q-values honoured), en by default.</summary>
    public static string PickLanguage(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage)) return "en";
        var ranked = acceptLanguage.Split(',')
            .Select((part, index) =>
            {
                var bits = part.Split(';');
                var tag = bits[0].Trim().ToLowerInvariant();
                var q = 1.0;
                foreach (var b in bits.Skip(1))
                {
                    var kv = b.Trim();
                    if (kv.StartsWith("q=", StringComparison.Ordinal) && double.TryParse(kv[2..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) q = parsed;
                }

                return (Lang: tag.Split('-')[0], Q: q, Index: index);
            })
            .Where(x => x.Q > 0)
            .OrderByDescending(x => x.Q).ThenBy(x => x.Index);
        foreach (var x in ranked)
        {
            if (Texts.ContainsKey(x.Lang)) return x.Lang;
        }

        return "en";
    }

    public static string FallbackPage(string lang, FallbackReason reason, string backHref)
    {
        if (!Texts.TryGetValue(lang, out var t)) { lang = "en"; t = Texts["en"]; }
        return FallbackTemplate.Value
            .Replace("{{lang}}", lang, StringComparison.Ordinal)
            .Replace("{{title}}", Html(t.Title), StringComparison.Ordinal)
            .Replace("{{reasonCode}}", reason == FallbackReason.NotLinked ? "notLinked" : "unreachable", StringComparison.Ordinal)
            .Replace("{{reason}}", Html(reason == FallbackReason.NotLinked ? t.NotLinked : t.Unreachable), StringComparison.Ordinal)
            .Replace("{{back}}", Html(t.Back), StringComparison.Ordinal)
            .Replace("{{backHref}}", Html(backHref), StringComparison.Ordinal);
    }

    // Escapes markup only, keeps accents readable (the page is UTF-8).
    private static string Html(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);

    private static byte[] ReadBytes(string name)
    {
        var assembly = typeof(MeResources).Assembly;
        using var stream = assembly.GetManifestResourceStream(typeof(Plugin).Namespace + "." + name)
            ?? throw new InvalidOperationException("Missing embedded resource " + name);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
