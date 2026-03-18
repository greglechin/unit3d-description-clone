using System.Net.Http.Headers;
using System.Net.Http.Json;
using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Models;
using Unit3dDescriptionClone.Serialization;

namespace Unit3dDescriptionClone.Services;

internal sealed class Unit3dApiClient(HttpClient client, AppConfig config) : ISourceTrackerClient
{
    public async Task<TorrentInfo?> GetTorrentAsync(string torrentId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{config.ToTrackerUrl}/api/torrents/{torrentId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ToTrackerApiKey);
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.TorrentInfo);
    }

    public async Task<TorrentInfo?> FindSourceTorrentAsync(string fileName, FromTrackerConfig fromTracker)
    {
        var url = $"{fromTracker.Url}/api/torrents/filter?file_name={Uri.EscapeDataString(fileName)}&perPage=1";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fromTracker.ApiKey);
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.TorrentsResponse);
        return result?.Data.FirstOrDefault();
    }

    public async Task<TorrentInfo?> FindSourceTorrentByTmdbIdAsync(int tmdbId, string fileName, FromTrackerConfig fromTracker)
    {
        var url = $"{fromTracker.Url}/api/torrents/filter?tmdbId={tmdbId}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fromTracker.ApiKey);
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.TorrentsResponse);
        var match = result?.Data.FirstOrDefault(d => d.Attributes.Files.Any(f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)));
        if (match is null)
            return null;

        var detailReq = new HttpRequestMessage(HttpMethod.Get, $"{fromTracker.Url}/api/torrents/{match.Id}");
        detailReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fromTracker.ApiKey);
        var detailResp = await client.SendAsync(detailReq);
        detailResp.EnsureSuccessStatusCode();
        return await detailResp.Content.ReadFromJsonAsync(AppJsonContext.Default.TorrentInfo);
    }

    public async Task<TorrentsResponse> GetTorrentsPageAsync(string url)
    {
        var resp = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ToTrackerApiKey);
            return req;
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.TorrentsResponse))!;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> buildRequest)
    {
        while (true)
        {
            var resp = await client.SendAsync(buildRequest());
            if ((int)resp.StatusCode != 429)
                return resp;

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
        }
    }

    async Task<SourceTorrentResult?> ISourceTrackerClient.FindSourceTorrentAsync(string fileName, FromTrackerConfig fromTracker)
    {
        var t = await FindSourceTorrentAsync(fileName, fromTracker);
        return t is null ? null : new SourceTorrentResult(t.Attributes.Description, t.Attributes.MediaInfo);
    }

    async Task<SourceTorrentResult?> ISourceTrackerClient.FindSourceTorrentByTmdbIdAsync(int tmdbId, string fileName, FromTrackerConfig fromTracker)
    {
        var t = await FindSourceTorrentByTmdbIdAsync(tmdbId, fileName, fromTracker);
        return t is null ? null : new SourceTorrentResult(t.Attributes.Description, t.Attributes.MediaInfo);
    }
}
