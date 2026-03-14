using System.Text;
using System.Text.Json;
using TR.BBCode.Parser;
using Unit3dDescriptionClone.Config;
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

        Console.WriteLine("Searching for matching torrent on source tracker...");
        var sourceTorrent = await api.FindSourceTorrentAsync(lookupFile.Name);
        if (sourceTorrent is null)
        {
            Console.WriteLine("No matching torrent found on source tracker, aborting.");
            return;
        }

        var description = new StringBuilder(sourceTorrent.Attributes.Description);
        var mediaInfo = sourceTorrent.Attributes.MediaInfo;

        if (!await RehostImagesAsync(description))
            return;

        TrimAfterLastImage(description);
        AppendDescriptionSuffix(description);

        await web.EnsureLoggedInAsync();
        await SubmitEditAsync(torrentId, description.ToString(), mediaInfo);
    }

    private async Task<bool> RehostImagesAsync(StringBuilder description)
    {
        var bbcode = BBCodeParser.Parse(description.ToString());
        var imgTags = bbcode.Where(p => p.Tags.ToList().Any(t => t.Name == "img")).ToList();
        Console.WriteLine($"Found {imgTags.Count} image(s) to rehost...");

        foreach (var imgTag in imgTags)
        {
            var imgUrl = imgTag.Content;
            var href = imgTag.Tags.FirstOrDefault(t => t.Name == "url")?.Attributes[""];

            if (config.KnownImages.TryGetValue(imgUrl, out var knownUrl))
            {
                Console.WriteLine($"  Skipping (known): {imgUrl} -> {knownUrl}");
                description.Replace(imgUrl, knownUrl);
                continue;
            }

            var newUrl = await imageRehoster.RehostAsync(imgUrl);
            if (newUrl is null)
                return false;

            description.Replace(imgUrl, newUrl);
            if (!string.IsNullOrEmpty(href))
                description.Replace(href, newUrl);
        }

        return true;
    }

    private static void TrimAfterLastImage(StringBuilder description)
    {
        var text = description.ToString();
        var lastImgUrlClose = text.LastIndexOf("[/img][/url]", StringComparison.OrdinalIgnoreCase);
        var lastImgClose = text.LastIndexOf("[/img]", StringComparison.OrdinalIgnoreCase);

        if (lastImgUrlClose >= 0)
        {
            var cutAt = lastImgUrlClose + "[/img][/url]".Length;
            description.Remove(cutAt, description.Length - cutAt);
        }
        else if (lastImgClose >= 0)
        {
            var cutAt = lastImgClose + "[/img]".Length;
            description.Remove(cutAt, description.Length - cutAt);
        }
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
