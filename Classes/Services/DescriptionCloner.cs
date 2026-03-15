using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Models;
using Unit3dDescriptionClone.Serialization;

namespace Unit3dDescriptionClone.Services;

internal sealed class DescriptionCloner(
    Unit3dApiClient api,
    Unit3dWebClient web,
    ImageRehoster imageRehoster,
    AppConfig config)
{
    private const string CacheDir = "cache";

    public async Task BackfillAsync(string releaseGroup)
    {
        Console.WriteLine($"Backfilling description for release group: {releaseGroup}");
        string? nextUrl = $"{config.ToTrackerUrl}/api/torrents/filter" +
            $"?name={Uri.EscapeDataString(releaseGroup)}&sortField=created_at&sortDirection=asc";

        while (nextUrl is not null)
        {
            var page = await api.GetTorrentsPageAsync(nextUrl);
            foreach (var torrent in page.Data)
            {
                var cacheFile = Path.Combine(CacheDir, $"{torrent.Id}.json");
                if (File.Exists(cacheFile))
                {
                    Console.WriteLine($"  Skipping (cached): {torrent.Id} - {torrent.Attributes.Name}");
                    continue;
                }

                await CloneAsync(torrent.Id);
                File.WriteAllText(cacheFile,
                    JsonSerializer.Serialize(torrent, AppJsonContext.Default.TorrentInfo));
            }

            nextUrl = page.Links?.Next;
            await Task.Delay(1000);
        }
    }

    public async Task CloneAsync(string torrentId)
    {
        Console.WriteLine($"Cloning description for torrent ID {torrentId}");

        Console.WriteLine($"Fetching torrent info from target tracker (ID {torrentId})...");
        var targetTorrent = await api.GetTorrentAsync(torrentId);
        var lookupFile = targetTorrent!.Attributes.Files.FirstOrDefault()!;
        Console.WriteLine($"Torrent name: {targetTorrent.Attributes.Name}");
        Console.WriteLine($"Lookup file:  {lookupFile.Name}");

        var fromTracker = config.GetFromTrackerForTorrent(targetTorrent.Attributes.Name);
        if (fromTracker is null)
        {
            Console.WriteLine("No matching [from_tracker] found for this torrent name, aborting.");
            return;
        }
        Console.WriteLine($"Using source tracker: {fromTracker.Url}");

        Console.WriteLine("Searching for matching torrent on source tracker...");
        TorrentInfo? sourceTorrent;
        if (!fromTracker.SupportsFileNameSearch)
        {
            var tmdbId = targetTorrent.Attributes.TmdbId;
            if (tmdbId is null or 0)
            {
                Console.WriteLine("No TMDB ID on target torrent, cannot search source tracker by TMDB ID, aborting.");
                return;
            }
            Console.WriteLine($"Source tracker does not support file_name search — searching by TMDB ID {tmdbId}...");
            sourceTorrent = await api.FindSourceTorrentByTmdbIdAsync(tmdbId.Value, lookupFile.Name, fromTracker);
        }
        else
        {
            sourceTorrent = await api.FindSourceTorrentAsync(lookupFile.Name, fromTracker);
        }
        if (sourceTorrent is null)
        {
            Console.WriteLine("No matching torrent found on source tracker, aborting.");
            return;
        }

        var description = new StringBuilder(sourceTorrent.Attributes.Description);
        var mediaInfo = sourceTorrent.Attributes.MediaInfo;

        if (!await RehostImagesAsync(description))
            return;

        AppendDescriptionSuffix(description);

        await web.EnsureLoggedInAsync();
        await SubmitEditAsync(torrentId, description.ToString(), mediaInfo);
    }

    private async Task<bool> RehostImagesAsync(StringBuilder description)
    {
        var str = description.ToString();

        var urlWrappedImgRegex = new Regex(
            @"\[url=(?<href>[^\]]*)\]\[img(?:=[^\]]*)?\](?<img>[^\[]*)\[/img\]\[/url\]",
            RegexOptions.IgnoreCase);
        var plainImgRegex = new Regex(
            @"\[img(?:=[^\]]*)?\](?<img>[^\[]*)\[/img\]",
            RegexOptions.IgnoreCase);
        var comparisonRegex = new Regex(
            @"\[comparison[^\]]*\](?<content>.*?)\[/comparison\]",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var urlRegex = new Regex(@"https?://\S+", RegexOptions.IgnoreCase);

        var images = new List<(string ImgUrl, string? HrefUrl)>();

        var urlWrappedMatches = urlWrappedImgRegex.Matches(str);
        foreach (Match m in urlWrappedMatches)
            images.Add((m.Groups["img"].Value, m.Groups["href"].Value));

        var coveredRanges = urlWrappedMatches.Cast<Match>()
            .Select(m => (Start: m.Index, End: m.Index + m.Length))
            .ToList();

        foreach (Match m in plainImgRegex.Matches(str))
        {
            if (!coveredRanges.Any(r => m.Index >= r.Start && m.Index < r.End))
                images.Add((m.Groups["img"].Value, null));
        }

        foreach (Match m in comparisonRegex.Matches(str))
            foreach (var sm in urlRegex.Matches(m.Groups["content"].Value).Select(u => u.Value))
                images.Add((sm, null));

        Console.WriteLine($"Found {images.Count} image(s) to rehost...");
        foreach (var (imgUrl, hrefUrl) in images)
        {
            if (config.KnownImages.TryGetValue(imgUrl, out var knownUrl))
            {
                Console.WriteLine($"  Skipping (known): {imgUrl} -> {knownUrl}");
                description.Replace(imgUrl, knownUrl);
                if (hrefUrl is not null && hrefUrl != imgUrl)
                    description.Replace(hrefUrl, knownUrl);
                continue;
            }

            var newUrl = await imageRehoster.RehostAsync(imgUrl);
            if (newUrl is null)
                return false;

            description.Replace(imgUrl, newUrl);
            if (hrefUrl is not null)
                description.Replace(hrefUrl, newUrl);
        }

        return true;
    }

    private static void AppendDescriptionSuffix(StringBuilder description)
    {
        if (!File.Exists("description_append.txt"))
            return;

        description.AppendLine();
        description.Append(File.ReadAllText("description_append.txt"));
    }

    private async Task SubmitEditAsync(string torrentId, string description, string? mediaInfo)
    {
        var editPageUrl = $"{config.ToTrackerUrl}/torrents/{torrentId}/edit";
        Console.WriteLine($"Fetching edit page for torrent {torrentId}...");
        var editHtml = await web.GetEditPageHtmlAsync(torrentId);
        var editForm = Unit3dWebClient.ParseEditPage(editHtml);

        editForm.Fields["description"] = description;
        if (!string.IsNullOrEmpty(mediaInfo) &&
            string.IsNullOrWhiteSpace(editForm.Fields.GetValueOrDefault("mediainfo")))
            editForm.Fields["mediainfo"] = mediaInfo;

        RemoveNonExistentExternalIds(editForm.Fields, editForm.AlpineExists);
        editForm.Fields.Remove("_token");
        editForm.Fields.Remove("_method");

        var patchData = new List<KeyValuePair<string, string>>
        {
            new("_token", editForm.Csrf!),
            new("_method", "PATCH"),
        };
        patchData.AddRange(editForm.Fields.Select(kvp => new KeyValuePair<string, string>(kvp.Key, kvp.Value)));

        if (editForm.Captcha is not null)
        {
            patchData.Add(new("_captcha", editForm.Captcha.Token));
            patchData.Add(new("_username", ""));
            patchData.Add(new(editForm.Captcha.RandomFieldName, editForm.Captcha.RandomFieldValue));
        }

        var patchResp = await web.SubmitEditFormAsync(torrentId, editPageUrl, patchData);
        Console.WriteLine(
            $"Patch response: {(int)patchResp.StatusCode} {patchResp.StatusCode} -> {patchResp.Headers.Location}");
    }

    private static void RemoveNonExistentExternalIds(
        Dictionary<string, string> fields,
        Dictionary<string, bool> alpineExists)
    {
        if (alpineExists.TryGetValue("tmdb_movie_exists", out var tme) && !tme) fields.Remove("movie_exists_on_tmdb");
        if (alpineExists.TryGetValue("tmdb_tv_exists", out var tte) && !tte) fields.Remove("tv_exists_on_tmdb");
        if (alpineExists.TryGetValue("imdb_title_exists", out var ite) && !ite) fields.Remove("title_exists_on_imdb");
        if (alpineExists.TryGetValue("tvdb_tv_exists", out var tvte) && !tvte) fields.Remove("tv_exists_on_tvdb");
        if (alpineExists.TryGetValue("mal_anime_exists", out var mae) && !mae) fields.Remove("anime_exists_on_mal");
        if (alpineExists.TryGetValue("igdb_game_exists", out var ige) && !ige) fields.Remove("game_exists_on_igdb");
    }
}
