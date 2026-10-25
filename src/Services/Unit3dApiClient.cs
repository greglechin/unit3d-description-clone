using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Models;
using Unit3dDescriptionClone.Serialization;

namespace Unit3dDescriptionClone.Services;

internal sealed class Unit3dApiClient(HttpClient client, AppConfig config) : ISourceTrackerClient
{
    public async Task<TorrentInfo?> GetTorrentAsync(string torrentId)
    {
        return await SendJsonWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{config.ToTrackerUrl}/api/torrents/{torrentId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ToTrackerApiKey);
            return req;
        }, AppJsonContext.Default.TorrentInfo, returnNullOnNotFound: true);
    }

    public async Task<TorrentInfo?> FindSourceTorrentAsync(string fileName, FromTrackerConfig fromTracker)
    {
        var url = $"{fromTracker.Url}/api/torrents/filter?file_name={Uri.EscapeDataString(fileName)}&perPage=1";
        var result = await SendJsonWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fromTracker.ApiKey);
            return req;
        }, AppJsonContext.Default.TorrentsResponse);
        return result?.Data.FirstOrDefault();
    }

    public async Task<TorrentInfo?> FindSourceTorrentByTmdbIdAsync(int tmdbId, string fileName, FromTrackerConfig fromTracker)
    {
        var url = $"{fromTracker.Url}/api/torrents/filter?tmdbId={tmdbId}";
        var result = await SendJsonWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fromTracker.ApiKey);
            return req;
        }, AppJsonContext.Default.TorrentsResponse);
        var match = result?.Data.FirstOrDefault(d => d.Attributes.Files.Any(f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)));
        if (match is null)
            return null;

        return await SendJsonWithRetryAsync(() =>
        {
            var detailReq = new HttpRequestMessage(HttpMethod.Get, $"{fromTracker.Url}/api/torrents/{match.Id}");
            detailReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fromTracker.ApiKey);
            return detailReq;
        }, AppJsonContext.Default.TorrentInfo);
    }

    async Task<SourceTorrentResult?> ISourceTrackerClient.FindSourceTorrentByIdAsync(string torrentId, FromTrackerConfig fromTracker)
    {
        var t = await SendJsonWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{fromTracker.Url}/api/torrents/{Uri.EscapeDataString(torrentId)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fromTracker.ApiKey);
            return req;
        }, AppJsonContext.Default.TorrentInfo, returnNullOnNotFound: true);
        if (t is null) return null;
        var (files, folder) = await GetSourceFilesAsync(t.Id, fromTracker);
        return new SourceTorrentResult(t.Id, t.Attributes.Description, t.Attributes.MediaInfo, folder, files);
    }

    public async Task<TorrentsResponse> GetTorrentsPageAsync(string url)
    {
        return (await SendJsonWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ToTrackerApiKey);
            return req;
        }, AppJsonContext.Default.TorrentsResponse, retryNotFound: true))!;
    }

    private async Task<T?> SendJsonWithRetryAsync<T>(
        Func<HttpRequestMessage> buildRequest,
        JsonTypeInfo<T> jsonTypeInfo,
        bool returnNullOnNotFound = false,
        bool retryNotFound = false)
    {
        for (var htmlRetry = 0; ; )
        {
            using var resp = await client.SendAsync(buildRequest());
            if ((int)resp.StatusCode == 429)
            {
                TimeSpan delay;
                if (resp.Headers.RetryAfter?.Delta is TimeSpan delta)
                    delay = delta;
                else if (resp.Headers.RetryAfter?.Date is DateTimeOffset date)
                    delay = date - DateTimeOffset.UtcNow;
                else
                    delay = TimeSpan.FromSeconds(60);

                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

                Console.WriteLine($"  Rate limited (429), retrying in {delay.TotalSeconds:0}s...");
                await Task.Delay(delay);
                continue;
            }

            if (returnNullOnNotFound && resp.StatusCode == HttpStatusCode.NotFound)
                return default;

            if (retryNotFound && resp.StatusCode == HttpStatusCode.NotFound && htmlRetry < 3)
            {
                htmlRetry++;
                var delay = TimeSpan.FromSeconds(htmlRetry * 5);
                Console.WriteLine($"  JSON API returned 404, retrying in {delay.TotalSeconds:0}s...");
                await Task.Delay(delay);
                continue;
            }

            try
            {
                if (resp.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                    throw new JsonException();

                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadFromJsonAsync(jsonTypeInfo);
            }
            catch (JsonException) when (htmlRetry < 3)
            {
                htmlRetry++;
                var delay = TimeSpan.FromSeconds(htmlRetry * 5);
                Console.WriteLine($"  JSON API returned HTML/invalid JSON, retrying in {delay.TotalSeconds:0}s...");
                await Task.Delay(delay);
            }
        }
    }

    async Task<SourceTorrentResult?> ISourceTrackerClient.FindSourceTorrentAsync(string fileName, FromTrackerConfig fromTracker)
    {
        var t = await FindSourceTorrentAsync(fileName, fromTracker);
        if (t is null) return null;
        var (files, folder) = await GetSourceFilesAsync(t.Id, fromTracker);
        return new SourceTorrentResult(t.Id, t.Attributes.Description, t.Attributes.MediaInfo, folder, files);
    }

    async Task<SourceTorrentResult?> ISourceTrackerClient.FindSourceTorrentByTmdbIdAsync(int tmdbId, string fileName, FromTrackerConfig fromTracker)
    {
        var t = await FindSourceTorrentByTmdbIdAsync(tmdbId, fileName, fromTracker);
        if (t is null) return null;
        var (files, folder) = await GetSourceFilesAsync(t.Id, fromTracker);
        return new SourceTorrentResult(t.Id, t.Attributes.Description, t.Attributes.MediaInfo, folder, files);
    }

    private async Task<(IReadOnlyList<TorrentFile> files, string? folder)> GetSourceFilesAsync(string torrentId, FromTrackerConfig fromTracker)
    {
        Console.WriteLine($"Downloading source torrent file (ID {torrentId})...");
        var (bytes, folder) = await DownloadTorrentFileAsync(torrentId, fromTracker);
        if (string.IsNullOrWhiteSpace(folder))
            folder = TorrentFileParser.GetFolderName(bytes);
        return (TorrentFileParser.GetFiles(bytes), folder);
    }

    private async Task<(byte[] bytes, string? folder)> DownloadTorrentFileAsync(string torrentId, FromTrackerConfig fromTracker)
    {
        var torrent = await SendJsonWithRetryAsync(() =>
        {
            var detailReq = new HttpRequestMessage(HttpMethod.Get, $"{fromTracker.Url}/api/torrents/{torrentId}");
            detailReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fromTracker.ApiKey);
            return detailReq;
        }, AppJsonContext.Default.TorrentInfo);
        var req = new HttpRequestMessage(HttpMethod.Get, torrent?.Attributes.DownloadLink);
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadAsByteArrayAsync(), torrent?.Attributes.Folder);
    }
}
