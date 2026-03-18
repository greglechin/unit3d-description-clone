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
        return searchItem is null ? null : await FetchDetailsAsync(fromTracker, searchItem.Id);
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
                return await FetchDetailsAsync(fromTracker, searchItem.Id);
        }
        return null;
    }

    private async Task<F3nixSearchItem?> SearchAsync(FromTrackerConfig fromTracker, Dictionary<string, string> fields)
    {
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

    private async Task<SourceTorrentResult?> FetchDetailsAsync(FromTrackerConfig fromTracker, int torrentId)
    {
        var url = $"{fromTracker.Url}/api/torrents/{Uri.EscapeDataString(fromTracker.ApiKey)}";
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["action"] = "details",
                ["torrent_id"] = torrentId.ToString(),
            })
        };
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.F3nixDetailsResponse);
        if (result?.Result is null)
            return null;
        return new SourceTorrentResult(result.Result.Description, result.Result.Mediainfo);
    }
}
