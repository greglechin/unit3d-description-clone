using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Models;
using Unit3dDescriptionClone.Serialization;

namespace Unit3dDescriptionClone.Services;

internal sealed class DescriptionCloner(
    Unit3dApiClient unit3dApi,
    F3nixApiClient f3nixApi,
    Unit3dWebClient web,
    ImageRehoster imageRehoster,
    AppConfig config)
{
    private const string CacheDir = "cache";

    public async Task BackfillAsync(string releaseGroup, string uploader, bool skipRehosting = false)
    {
        Console.WriteLine($"Backfilling description for release group: {releaseGroup}, uploader: {uploader}");
        string? nextUrl = $"{config.ToTrackerUrl}/api/torrents/filter" +
            $"?name={Uri.EscapeDataString(releaseGroup)}&uploader={Uri.EscapeDataString(uploader)}&sortField=created_at&sortDirection=asc";

        while (nextUrl is not null)
        {
            var page = await unit3dApi.GetTorrentsPageAsync(nextUrl);
            foreach (var torrent in page.Data)
            {
                var cacheFile = Path.Combine(CacheDir, $"{torrent.Id}.json");
                if (File.Exists(cacheFile))
                {
                    Console.WriteLine($"  Skipping (cached): {torrent.Id} - {torrent.Attributes.Name}");
                    continue;
                }

                await CloneAsync(torrent.Id, skipRehosting);
                File.WriteAllText(cacheFile,
                    JsonSerializer.Serialize(torrent, AppJsonContext.Default.TorrentInfo));
            }

            nextUrl = page.Links?.Next;
            await Task.Delay(1000);
        }
    }

    public async Task CloneAsync(string torrentId, bool skipRehosting = false)
    {
        Console.WriteLine($"Cloning description for torrent ID {torrentId}");

        Console.WriteLine($"Fetching torrent info from target tracker (ID {torrentId})...");
        var targetTorrent = await unit3dApi.GetTorrentAsync(torrentId);
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
        ISourceTrackerClient sourceClient = fromTracker.TrackerType == TrackerType.F3NIX ? f3nixApi : unit3dApi;
        SourceTorrentResult? sourceResult;
        if (!fromTracker.SupportsFileNameSearch)
        {
            var tmdbId = targetTorrent.Attributes.TmdbId;
            if (tmdbId is null or 0)
            {
                Console.WriteLine("No TMDB ID on target torrent, cannot search source tracker by TMDB ID, aborting.");
                return;
            }
            Console.WriteLine($"Source tracker does not support file_name search — searching by TMDB ID {tmdbId}...");
            sourceResult = await sourceClient.FindSourceTorrentByTmdbIdAsync(tmdbId.Value, lookupFile.Name, fromTracker);
        }
        else
        {
            sourceResult = await sourceClient.FindSourceTorrentAsync(lookupFile.Name, fromTracker);
        }
        if (sourceResult is null)
        {
            Console.WriteLine("No matching torrent found on source tracker, aborting.");
            return;
        }

        var description = new StringBuilder(sourceResult.Description);
        var mediaInfo = sourceResult.MediaInfo;

        StripLines(description);

        var oldTargetDescription = targetTorrent.Attributes.Description ?? "";

        // don't ask
        description = description.Replace("h:m:s", "h:​m:s");

        description = description.Replace("[hide", "[spoiler");
        description = description.Replace("[/hide]", "[/spoiler]");

        description = ReplaceAlignTags(description);

        var wrappedSpoilerTag = "[spoiler=original info]";
        string? originalDescriptionSpoiler = null;
        if (oldTargetDescription.Contains(wrappedSpoilerTag, StringComparison.OrdinalIgnoreCase))
        {
            var lastEndTagIndex = oldTargetDescription.LastIndexOf("[/spoiler]", StringComparison.OrdinalIgnoreCase);
            var startTagIndex = oldTargetDescription.LastIndexOf(wrappedSpoilerTag, lastEndTagIndex, StringComparison.OrdinalIgnoreCase);
            if (startTagIndex >= 0 && lastEndTagIndex > startTagIndex)
            {
                var removeLength = lastEndTagIndex + "[/spoiler]".Length - startTagIndex;
                originalDescriptionSpoiler = oldTargetDescription.Substring(startTagIndex, removeLength);
                oldTargetDescription = oldTargetDescription.Remove(startTagIndex, removeLength);
            }
        }
        else if (!string.IsNullOrWhiteSpace(oldTargetDescription))
            originalDescriptionSpoiler = $"{wrappedSpoilerTag}{oldTargetDescription}[/spoiler]";


        description.Insert(0, "[code]");
        description.Append("[/code]");

        if (!skipRehosting && !await RehostImagesAsync(description))
            return;

        if (originalDescriptionSpoiler is not null)
            description.Append(originalDescriptionSpoiler);

        if (skipRehosting)
            Console.WriteLine("Skipping image rehosting (--no-rehost).");

        AppendDescriptionSuffix(description);

        await web.EnsureLoggedInAsync();
        await SubmitEditAsync(torrentId, description.ToString(), mediaInfo);
    }

    private void StripLines(StringBuilder description)
    {
        if (config.StripLinePatterns.Count == 0)
            return;

        var lines = description.ToString().Split('\n');
        var filtered = lines.Where(line =>
            !config.StripLinePatterns.Any(rx => rx.IsMatch(line)));
        var result = string.Join('\n', filtered);
        description.Clear();
        description.Append(result);
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

        images = [.. images.Where(i => !i.ImgUrl.Contains(config.ImageHostUrl, StringComparison.OrdinalIgnoreCase))];

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

            var fetchUrl = imgUrl;
            if (hrefUrl is not null && await imageRehoster.CheckUrlIsImage(hrefUrl))
                fetchUrl = hrefUrl;

            var newUrl = await imageRehoster.RehostAsync(fetchUrl);
            if (newUrl is null)
                return false;

            if (hrefUrl is not null)
            {
                description.Replace("[url=" + hrefUrl + "]", "[url=" + newUrl + "]");
                description.Replace(imgUrl, newUrl + $"?variant=thumb");
            } 
            else
            {
                description.Replace(imgUrl, newUrl);
            }
        }

        return true;
    }

    private void AppendDescriptionSuffix(StringBuilder description)
    {
        if (string.IsNullOrEmpty(config.DescriptionAppend))
            return;

        description.AppendLine();
        description.Append(config.DescriptionAppend);
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

        var formMediaInfo = LayeredHtmlDecode(editForm.Fields.GetValueOrDefault("mediainfo") ?? "");
        if (!string.IsNullOrWhiteSpace(formMediaInfo))
            editForm.Fields["mediainfo"] = formMediaInfo;

        var formName = LayeredHtmlDecode(editForm.Fields.GetValueOrDefault("name") ?? "");
        if (!string.IsNullOrWhiteSpace(formName))
            editForm.Fields["name"] = formName;

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

    private static string LayeredHtmlDecode(string formValue)
    {
        string decodedFormValue;
        while ((decodedFormValue = HttpUtility.HtmlDecode(formValue)) != formValue)
            formValue = decodedFormValue;
        return formValue;
    }

    private static readonly HashSet<string> KnownAlignValues =
        new(StringComparer.OrdinalIgnoreCase) { "left", "center", "right" };

    private static StringBuilder ReplaceAlignTags(StringBuilder text)
    {
        var tagRegex = new Regex(@"\[align=(?<val>[^\]]+)\]|\[/align\]", RegexOptions.IgnoreCase);
        var matches = tagRegex.Matches(text.ToString());
        var stack = new Stack<(int Index, int Length, string Value)>();
        var replacements = new List<(int Index, int Length, string Replacement)>();

        foreach (Match m in matches)
        {
            if (m.Groups["val"].Success)
            {
                stack.Push((m.Index, m.Length, m.Groups["val"].Value));
            }
            else if (stack.Count > 0)
            {
                var (openIndex, openLen, openVal) = stack.Pop();
                if (KnownAlignValues.Contains(openVal))
                {
                    var tag = openVal.ToLowerInvariant();
                    replacements.Add((openIndex, openLen, $"[{tag}]"));
                    replacements.Add((m.Index, m.Length, $"[/{tag}]"));
                }
            }
        }

        if (replacements.Count == 0)
            return text;

        foreach (var (index, length, replacement) in replacements.OrderByDescending(r => r.Index))
        {
            text.Remove(index, length);
            text.Insert(index, replacement);
        }
        return text;
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
