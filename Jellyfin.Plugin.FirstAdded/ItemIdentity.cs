using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.FirstAdded;

public sealed record ItemIdentity(string Kind, List<string> Keys);

public static class IdentityBuilder
{
    public static ItemIdentity? Build(BaseItem item, ILibraryManager library)
    {
        if (item.IsVirtualItem || string.IsNullOrWhiteSpace(item.Path)) return null;
        var keys = new List<string>();
        if (item is Movie)
        {
            AddProviderKeys(keys, "movie", item);
            return keys.Count == 0 ? null : new("Movie", keys.Order(StringComparer.Ordinal).ToList());
        }
        if (item is not Episode episode) return null;
        // A combined episode file cannot represent different first-added dates for each episode.
        // Preserve it rather than hiding a newly acquired episode when a pack is split/merged.
        if (episode.IndexNumber is not >= 0 || episode.ParentIndexNumber is not >= 0
            || (episode.IndexNumberEnd is { } end && end != episode.IndexNumber)) return null;
        AddProviderKeys(keys, "episode", item);
        if (library.GetItemById(episode.SeriesId) is Series series)
        {
            var seriesKeys = new List<string>();
            AddProviderKeys(seriesKeys, "series", series);
            foreach (var key in seriesKeys)
                keys.Add("episode:" + key + ":s" + episode.ParentIndexNumber.Value.ToString(CultureInfo.InvariantCulture)
                    + ":e" + episode.IndexNumber.Value.ToString(CultureInfo.InvariantCulture));
        }
        return keys.Count == 0 ? null : new("Episode", keys.Order(StringComparer.Ordinal).ToList());
    }
    private static void AddProviderKeys(List<string> keys, string kind, BaseItem item)
    {
        foreach (var provider in new[] { MetadataProvider.Tmdb, MetadataProvider.Tvdb })
        {
            var value = item.GetProviderId(provider);
            if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                keys.Add(kind + ":" + provider.ToString().ToLowerInvariant() + "=" + id.ToString(CultureInfo.InvariantCulture));
        }
        var imdb = item.GetProviderId(MetadataProvider.Imdb)?.Trim().ToLowerInvariant();
        if (imdb is { Length: > 2 } && imdb.StartsWith("tt", StringComparison.Ordinal) && imdb[2..].All(char.IsAsciiDigit))
            keys.Add(kind + ":imdb=" + imdb);
    }
    public static bool Compatible(IEnumerable<string> existing, IEnumerable<string> incoming)
    {
        var byNamespace = existing.GroupBy(k => k[..k.LastIndexOf('=')], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        return incoming.All(k => !byNamespace.TryGetValue(k[..k.LastIndexOf('=')], out var known) || known.Contains(k));
    }
    public static DateTime Utc(DateTime date) => date.Kind == DateTimeKind.Local ? date.ToUniversalTime()
        : DateTime.SpecifyKind(date, DateTimeKind.Utc);
}
