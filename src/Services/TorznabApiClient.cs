using System.Xml.Linq;
using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Models;

namespace Unit3dDescriptionClone.Services;

internal sealed class TorznabApiClient(HttpClient client) : ISourceTrackerClient
{
    public async Task<SourceTorrentResult?> FindSourceTorrentAsync(string fileName, FromTrackerConfig fromTracker)
    {
        var item = await SearchByFileNameAsync(fromTracker, fileName);
        return item is null ? null : await FetchDetailsAsync(fromTracker, item);
    }

    public async Task<SourceTorrentResult?> FindSourceTorrentByTmdbIdAsync(int tmdbId, string fileName, FromTrackerConfig fromTracker)
    {
        var expectedTitle = Path.GetFileNameWithoutExtension(fileName);
        foreach (var type in new[] { "movie", "tvsearch" })
        {
            var items = await GetItemsAsync(fromTracker, type, new Dictionary<string, string>
            {
                ["tmdbid"] = tmdbId.ToString(),
            });
            if (items.Count == 0)
                continue;

            foreach (var item in OrderByTitleMatch(items, expectedTitle))
            {
                var result = await FetchDetailsAsync(fromTracker, item);
                if (result is not null && result.Files.Any(file =>
                    Path.GetFileName(file.Name).Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                    return result;
            }
        }

        return null;
    }

    private async Task<TorznabItem?> SearchByFileNameAsync(FromTrackerConfig fromTracker, string fileName)
    {
        foreach (var query in GetSearchQueries(fileName))
        {
            var items = await GetItemsAsync(fromTracker, "search", new Dictionary<string, string>
            {
                ["q"] = query,
            });
            if (items.Count == 0)
                continue;

            var exact = items.FirstOrDefault(item =>
                item.Title.Equals(query, StringComparison.OrdinalIgnoreCase));
            return exact ?? items[0];
        }

        return null;
    }

    private async Task<SourceTorrentResult?> FetchDetailsAsync(FromTrackerConfig fromTracker, TorznabItem searchItem)
    {
        var item = searchItem;
        if (!string.IsNullOrWhiteSpace(searchItem.Id))
        {
            var detailsItem = (await GetItemsAsync(fromTracker, "details", new Dictionary<string, string>
            {
                ["id"] = searchItem.Id,
            })).FirstOrDefault();
            if (detailsItem is not null)
                item = detailsItem with { DownloadUrl = detailsItem.DownloadUrl ?? searchItem.DownloadUrl };
        }

        if (string.IsNullOrWhiteSpace(item.DownloadUrl))
            throw new InvalidDataException("Torznab item did not include a torrent download URL.");

        Console.WriteLine($"Downloading source torrent file (ID {item.Id})...");
        var torrentFile = await DownloadTorrentFileAsync(fromTracker, item.DownloadUrl);
        var description = item.Description;
        if (fromTracker.GrabNfoFromTorrentFile)
        {
            var nfo = TorrentFileParser.GetNfo(torrentFile);
            if (!string.IsNullOrWhiteSpace(nfo))
                description = nfo;
            else
                Console.WriteLine("  No NFO found in torrent file metadata; using Torznab description.");
        }

        var (cleanDescription, mediaInfo) = ExtractMediaInfo(description);
        return new SourceTorrentResult(
            item.Id,
            cleanDescription,
            mediaInfo,
            TorrentFileParser.GetFolderName(torrentFile),
            TorrentFileParser.GetFiles(torrentFile));
    }

    private async Task<IReadOnlyList<TorznabItem>> GetItemsAsync(
        FromTrackerConfig fromTracker,
        string type,
        Dictionary<string, string> query)
    {
        query["apikey"] = fromTracker.ApiKey;
        query["t"] = type;

        var req = new HttpRequestMessage(HttpMethod.Get, BuildApiUrl(fromTracker, query));
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var xml = await resp.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);
        return [.. doc.Descendants()
            .Where(e => e.Name.LocalName == "item")
            .Select(item => ParseItem(item, fromTracker))];
    }

    private static TorznabItem ParseItem(XElement item, FromTrackerConfig fromTracker)
    {
        var guid = Text(item, "guid");
        var link = Text(item, "link");
        var comments = Text(item, "comments");
        var enclosureUrl = Child(item, "enclosure")?.Attribute("url")?.Value;
        var id = TryGetTorrentId(guid)
            ?? TryGetTorrentId(comments)
            ?? TryGetQueryValue(link, "id")
            ?? "";

        return new TorznabItem(
            id,
            Text(item, "title"),
            Text(item, "description"),
            ToAbsoluteUrl(enclosureUrl ?? link, fromTracker.Url));
    }

    private static string BuildApiUrl(FromTrackerConfig fromTracker, IReadOnlyDictionary<string, string> query) =>
        $"{fromTracker.Url.TrimEnd('/')}/api/torznab?{string.Join('&', query.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"))}";

    private static IReadOnlyList<string> GetSearchQueries(string fileName)
    {
        var result = new List<string> { fileName };
        var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
        if (!string.IsNullOrWhiteSpace(withoutExtension) &&
            !result.Contains(withoutExtension, StringComparer.OrdinalIgnoreCase))
            result.Add(withoutExtension);

        return result;
    }

    private static IEnumerable<TorznabItem> OrderByTitleMatch(IReadOnlyList<TorznabItem> items, string expectedTitle) =>
        items.OrderByDescending(item =>
            item.Title.Equals(expectedTitle, StringComparison.OrdinalIgnoreCase));

    private static (string Description, string? MediaInfo) ExtractMediaInfo(string description)
    {
        const string startTag = "[mediainfo]";
        const string endTag = "[/mediainfo]";

        var start = description.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return (description, null);

        var contentStart = start + startTag.Length;
        var end = description.IndexOf(endTag, contentStart, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return (description, null);

        var mediaInfo = description[contentStart..end].Trim();
        var cleaned = (description[..start] + description[(end + endTag.Length)..]).Trim();
        return (cleaned, string.IsNullOrWhiteSpace(mediaInfo) ? null : mediaInfo);
    }

    private async Task<byte[]> DownloadTorrentFileAsync(FromTrackerConfig fromTracker, string downloadUrl)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, ToAbsoluteUrl(downloadUrl, fromTracker.Url));
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        if (bytes.Length > 0 && bytes[0] == (byte)'d')
            return bytes;

        throw new InvalidDataException("Torznab download response was not a .torrent file.");
    }

    private static string Text(XElement item, string name) =>
        Child(item, name)?.Value.Trim() ?? "";

    private static XElement? Child(XElement item, string name) =>
        item.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static string? TryGetTorrentId(string? value) =>
        TryGetQueryValue(value, "torrentid");

    private static string? TryGetQueryValue(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var uri = new Uri(value, UriKind.RelativeOrAbsolute);
        var query = uri.IsAbsoluteUri ? uri.Query : GetRelativeQuery(value);
        if (query.StartsWith('?'))
            query = query[1..];

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var partKey = eq < 0 ? part : part[..eq];
            if (!partKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            return Uri.UnescapeDataString(eq < 0 ? "" : part[(eq + 1)..]);
        }

        return null;
    }

    private static string GetRelativeQuery(string value)
    {
        var queryIndex = value.IndexOf('?');
        return queryIndex < 0 ? "" : value[queryIndex..];
    }

    private static string ToAbsoluteUrl(string? url, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";

        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"{baseUrl.TrimEnd('/')}/{url.TrimStart('/')}";
    }

    private sealed record TorznabItem(string Id, string Title, string Description, string? DownloadUrl);
}
