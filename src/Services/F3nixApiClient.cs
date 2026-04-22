using System.Net.Http.Json;
using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Models;
using Unit3dDescriptionClone.Serialization;

namespace Unit3dDescriptionClone.Services;

internal sealed class F3nixApiClient(HttpClient client) : ISourceTrackerClient
{
    public async Task<SourceTorrentResult?> FindSourceTorrentAsync(string fileName, FromTrackerConfig fromTracker)
    {
        var searchItem = await SearchAsync(fromTracker, new Dictionary<string, string>
        {
            ["action"] = "search",
            ["file_name"] = fileName,
        });
        return searchItem is null ? null : await FetchDetailsAsync(fromTracker, searchItem);
    }

    public async Task<SourceTorrentResult?> FindSourceTorrentByTmdbIdAsync(int tmdbId, string fileName, FromTrackerConfig fromTracker)
    {
        foreach (var prefix in new[] { "movie", "tv" })
        {
            var searchItem = await SearchAsync(fromTracker, new Dictionary<string, string>
            {
                ["action"] = "search",
                ["tmdb_id"] = $"{prefix}/{tmdbId}",
            });
            if (searchItem is not null)
                return await FetchDetailsAsync(fromTracker, searchItem);
        }
        return null;
    }

    private async Task<F3nixSearchItem?> SearchAsync(FromTrackerConfig fromTracker, Dictionary<string, string> fields)
    {
        AddRssKey(fromTracker, fields);
        var url = $"{fromTracker.Url}/api/torrents/{Uri.EscapeDataString(fromTracker.ApiKey)}";
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.F3nixSearchResponse);
        return result?.Results.FirstOrDefault();
    }

    private async Task<SourceTorrentResult?> FetchDetailsAsync(FromTrackerConfig fromTracker, F3nixSearchItem searchItem)
    {
        var url = $"{fromTracker.Url}/api/torrents/{Uri.EscapeDataString(fromTracker.ApiKey)}";
        var fields = new Dictionary<string, string>
        {
            ["action"] = "details",
            ["torrent_id"] = searchItem.Id.ToString(),
        };
        AddRssKey(fromTracker, fields);
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.F3nixDetailsResponse);
        if (result?.Result is null)
            return null;
        return new SourceTorrentResult(
            result.Result.Id.ToString(),
            result.Result.Description,
            result.Result.Mediainfo,
            await GetSourceFilesAsync(fromTracker, result.Result.Id, result.Result.DownloadUrl ?? searchItem.DownloadUrl));
    }

    private static void AddRssKey(FromTrackerConfig fromTracker, Dictionary<string, string> fields)
    {
        if (!string.IsNullOrWhiteSpace(fromTracker.RssKey))
            fields["rsskey"] = fromTracker.RssKey;
    }

    private async Task<IReadOnlyList<TorrentFile>> GetSourceFilesAsync(
        FromTrackerConfig fromTracker,
        int torrentId,
        string? downloadUrl)
    {
        Console.WriteLine($"Downloading source torrent file (ID {torrentId})...");
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidDataException("F3NIX details response did not include download_url.");

        return TorrentFileParser.GetFiles(await DownloadTorrentFileAsync(fromTracker, downloadUrl));
    }

    private async Task<byte[]> DownloadTorrentFileAsync(FromTrackerConfig fromTracker, string downloadUrl)
    {
        var absoluteUrl = downloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? downloadUrl
            : $"{fromTracker.Url.TrimEnd('/')}/{downloadUrl.TrimStart('/')}";
        var req = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        if (bytes.Length > 0 && bytes[0] == (byte)'d')
            return bytes;

        throw new InvalidDataException("F3NIX download_url response was not a .torrent file.");
    }
}
