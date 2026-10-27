using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Models;
using Unit3dDescriptionClone.Serialization;

namespace Unit3dDescriptionClone.Services;

internal enum CloneOutcome
{
    Success,
    AlreadyCloned,
    Failed,
}

internal sealed class DescriptionCloner(
    Unit3dApiClient unit3dApi,
    F3nixApiClient f3nixApi,
    TorznabApiClient torznabApi,
    Unit3dWebClient web,
    ImageRehoster imageRehoster,
    AppConfig config)
{
    private const string CacheDir = "cache";
    private const string OriginalInfoSpoilerTag = "[spoiler=original info]";

    public async Task BackfillAsync(string releaseGroup, string uploader, bool skipRehosting = false, bool skipAppend = false, bool allowRerun = false)
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

                var outcome = await CloneAsync(torrent.Id, skipRehosting, skipAppend, allowRerun);
                File.WriteAllText(cacheFile,
                    JsonSerializer.Serialize(torrent, AppJsonContext.Default.TorrentInfo));
                if (outcome is CloneOutcome.Success or CloneOutcome.AlreadyCloned)
                    await Task.Delay(2000);
            }

            nextUrl = page.Links?.Next;
            await Task.Delay(5000);
        }
    }

    public async Task<CloneOutcome> CloneAsync(
        string torrentId,
        bool skipRehosting = false,
        bool skipAppend = false,
        bool allowRerun = false,
        string? fromTrackerName = null,
        string? fromTorrentId = null)
    {
        var targetTorrent = await unit3dApi.GetTorrentAsync(torrentId);
        if (targetTorrent == null)
        {
            Console.WriteLine($"Cloning {config.ToTrackerUrl}/torrents/{torrentId}...");
            Console.WriteLine("Target torrent not found on api");
            return CloneOutcome.Failed;
        }
        Console.WriteLine($"Cloning {config.ToTrackerUrl}/torrents/{torrentId} ({targetTorrent.Attributes.Name}) ...");
        if (!allowRerun && targetTorrent!.Attributes.Description.Contains(OriginalInfoSpoilerTag, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Target description already contains original info spoiler, skipping. Use --allow-rerun to override.");
            return CloneOutcome.AlreadyCloned;
        }

        var lookupFile = targetTorrent!.Attributes.Files.FirstOrDefault();
        if (lookupFile == null)
        {
            Console.WriteLine("Target does not report any files for comparison");
            return CloneOutcome.Failed;
        }
        var lookupFileName = Path.GetFileName(lookupFile.Name);
        Console.WriteLine($"Lookup file: {lookupFileName}");

        async Task MarkTrumpableAsync(string reason, string? appendTag = null)
        {
            var description = new StringBuilder()
                .Append("[code]")
                .AppendLine(reason)
                .AppendLine("[/code]");
            if (!string.IsNullOrWhiteSpace(targetTorrent.Attributes.Description))
            {
                description.AppendLine();
                description.Append(targetTorrent.Attributes.Description);
            }

            await web.EnsureLoggedInAsync();
            await SubmitEditAsync(
                torrentId,
                description.ToString(),
                targetTorrent.Attributes.MediaInfo,
                GetTrumpableName(targetTorrent.Attributes.Name) + (appendTag ?? ""));
        }

        if (IsTrumpableName(targetTorrent.Attributes.Name))
        {
            Console.WriteLine("Torrent already marked -TRUMPABLE");
            return CloneOutcome.Failed;
        }

        IReadOnlyList<FromTrackerConfig> fromTrackers = fromTrackerName is null
            ? config.GetFromTrackersForTorrent(targetTorrent.Attributes.Name)
            : [.. config.FromTrackers.Where(fromTracker =>
                fromTracker.Url.Contains(fromTrackerName, StringComparison.OrdinalIgnoreCase))];
        if (fromTrackers.Count == 0)
        {
            if (fromTrackerName is null)
            {
                Console.WriteLine("No matching [from_tracker] found for this torrent name");
            }
            else
            {
                Console.WriteLine($"No [from_tracker] named '{fromTrackerName}' found");
            }
            return CloneOutcome.Failed;
        }
        SourceTorrentResult? sourceResult = null;
        foreach (var fromTracker in fromTrackers)
        {
            Console.WriteLine($"Searching for matching torrent on source tracker: {fromTracker.Url}");
            ISourceTrackerClient sourceClient = fromTracker.TrackerType switch
            {
                TrackerType.F3NIX => f3nixApi,
                TrackerType.TORZNAB => torznabApi,
                _ => unit3dApi,
            };
            if (fromTorrentId is not null)
            {
                Console.WriteLine($"Fetching source torrent by known ID {fromTorrentId}...");
                sourceResult = await sourceClient.FindSourceTorrentByIdAsync(fromTorrentId, fromTracker);
            }
            else if (!fromTracker.SupportsFileNameSearch)
            {
                var tmdbId = targetTorrent.Attributes.TmdbId;
                var imdbId = targetTorrent.Attributes.ImdbId;
                if (fromTracker.TrackerType == TrackerType.TORZNAB)
                {
                    if (imdbId is null or 0)
                    {
                        Console.WriteLine("No IMDb ID on target torrent, cannot search this Torznab source tracker.");
                        continue;
                    }
                    Console.WriteLine($"Source tracker does not support file_name search — searching Torznab by IMDb ID {imdbId}...");
                    sourceResult = await torznabApi.FindSourceTorrentByImdbIdAsync(
                        imdbId.Value,
                        lookupFileName,
                        fromTracker,
                        targetTorrent.Attributes.Category.Equals("TV Show", StringComparison.OrdinalIgnoreCase) ? 5000 : 2000);
                }
                else
                {
                    if (tmdbId is null or 0)
                    {
                        Console.WriteLine("No TMDB ID on target torrent, cannot search this source tracker by external ID.");
                        continue;
                    }
                    sourceResult = await sourceClient.FindSourceTorrentByTmdbIdAsync(tmdbId.Value, lookupFileName, fromTracker);
                }
            }
            else
            {
                sourceResult = await sourceClient.FindSourceTorrentAsync(lookupFileName, fromTracker);
            }

            if (sourceResult is not null)
                break;

            Console.WriteLine("No matching torrent found on this source tracker.");
        }
        if (sourceResult is null)
        {
            await MarkTrumpableAsync("No matching torrent found on any source tracker.", "-TOREVIEW");
            Console.WriteLine("No matching torrent found on any source tracker.");
            return CloneOutcome.Failed;
        }

        var isTrumpable = IsTrumpable(targetTorrent.Attributes, sourceResult, out var trumpableReason);
        if (isTrumpable)
        {
            await MarkTrumpableAsync(trumpableReason!);
            Console.WriteLine(trumpableReason);
            return CloneOutcome.Failed;
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

        string? originalDescriptionSpoiler = null;
        if (oldTargetDescription.Contains(OriginalInfoSpoilerTag, StringComparison.OrdinalIgnoreCase))
        {
            var lastEndTagIndex = oldTargetDescription.LastIndexOf("[/spoiler]", StringComparison.OrdinalIgnoreCase);
            var startTagIndex = oldTargetDescription.LastIndexOf(OriginalInfoSpoilerTag, lastEndTagIndex, StringComparison.OrdinalIgnoreCase);
            if (startTagIndex >= 0 && lastEndTagIndex > startTagIndex)
            {
                var removeLength = lastEndTagIndex + "[/spoiler]".Length - startTagIndex;
                originalDescriptionSpoiler = oldTargetDescription.Substring(startTagIndex, removeLength);
                oldTargetDescription = oldTargetDescription.Remove(startTagIndex, removeLength);
            }
        }
        else if (!string.IsNullOrWhiteSpace(oldTargetDescription))
            originalDescriptionSpoiler = $"{OriginalInfoSpoilerTag}{oldTargetDescription}[/spoiler]";


        description.Insert(0, "[code]");
        description.Append("[/code]");

        if (!skipRehosting)
        {
            var (rehosted, dead, unavailable, deadHostRemoved) = await RehostImagesAsync(description);
            Console.WriteLine($"Image rehosting: {rehosted} rehosted, {dead} confirmed dead (left as-is), " +
                $"{unavailable} temporarily unavailable (left as-is), {deadHostRemoved} removed entirely (confirmed-dead host).");
        }

        if (originalDescriptionSpoiler is not null)
            description.Append(originalDescriptionSpoiler);

        if (skipRehosting)
            Console.WriteLine("Skipping image rehosting (--no-rehost).");

        if (skipAppend)
            Console.WriteLine("Skipping description append (--no-append).");
        else
            AppendDescriptionSuffix(description);

        await web.EnsureLoggedInAsync();
        await SubmitEditAsync(torrentId, description.ToString(), mediaInfo, null);
        Console.WriteLine($"Success");
        return CloneOutcome.Success;
    }

    private static bool IsTrumpable(TorrentAttributes targetTorrent, SourceTorrentResult sourceTorrent, out string? reason)
    {
        reason = null;
        var targetFiles = targetTorrent.Files;
        var sourceFiles = sourceTorrent.Files;
        targetFiles = [.. targetFiles.Where(file => file.Name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))];
        sourceFiles = [.. sourceFiles.Where(file => file.Name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))];
        Console.WriteLine($"Validating {targetFiles.Count} target MKV file(s) against source torrent...");
        if (sourceFiles.Count != targetFiles.Count)
        {
            reason = $"Source file count mismatch: target={targetFiles.Count} source={sourceFiles.Count}";
            Console.WriteLine($"  {reason}");
            return true;
        }

        if (targetFiles.Any(file => GetTargetPathDepth(targetTorrent.Folder, file.Name) > 1))
        {
            reason = "Target MKV file(s) are more than 1 folder deep.";
            Console.WriteLine($"  {reason}");
            return true;
        }

        if (!FoldersMatch(targetTorrent.Folder, sourceTorrent.Folder))
        {
            reason = $"Source folder mismatch: target={targetTorrent.Folder} source={sourceTorrent.Folder}";
            Console.WriteLine($"  {reason}");
            return true;
        }

        foreach (var targetFile in targetFiles)
        {
            var sourceFile = FindSourceFile(targetFile.Name, sourceFiles);
            if (sourceFile is null)
            {
                reason = $"Source file missing: {targetFile.Name}";
                Console.WriteLine($"  {reason}");
                return true;
            }

            if (sourceFile.Size != targetFile.Size)
            {
                reason = $"Source file size mismatch: {targetFile.Name} target={targetFile.Size} source={sourceFile.Size}";
                Console.WriteLine($"  {reason}");
                return true;
            }
        }

        var extraFiles = targetTorrent.Files
            .Where(file => !file.Name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            .Where(file => FindSourceFile(file.Name, sourceTorrent.Files) is null)
            .ToList();
        if (extraFiles.Count > 0)
        {
            reason = "Extra files unmatched at source: " + string.Join(", ", extraFiles.Select(file => file.Name));
            Console.WriteLine($"  {reason}");
            return true;
        }

        var sourceUniqueId = Regex.Match(sourceTorrent.MediaInfo ?? "", @"^\s*Unique\s*ID\s*:\s*(?<id>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (sourceUniqueId.Success)
        {
            var sourceCompleteName = Regex.Match(sourceTorrent.MediaInfo ?? "", @"^\s*Complete\s*name\s*:\s*(?<name>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            var targetCompleteName = Regex.Match(targetTorrent.MediaInfo ?? "", @"^\s*Complete\s*name\s*:\s*(?<name>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (sourceCompleteName.Success && targetCompleteName.Success &&
                GetTorrentFileName(NormalizeTorrentPath(targetCompleteName.Groups["name"].Value.Trim()))
                    .Equals(GetTorrentFileName(NormalizeTorrentPath(sourceCompleteName.Groups["name"].Value.Trim())), StringComparison.OrdinalIgnoreCase))
            {
                var targetUniqueId = Regex.Match(targetTorrent.MediaInfo ?? "", @"^\s*Unique\s*ID\s*:\s*(?<id>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (!targetUniqueId.Success || !targetUniqueId.Groups["id"].Value.Trim().Equals(sourceUniqueId.Groups["id"].Value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"MediaInfo UniqueID mismatch: target={targetUniqueId.Groups["id"].Value.Trim()} source={sourceUniqueId.Groups["id"].Value.Trim()}";
                    Console.WriteLine($"  {reason}");
                    return true;
                }
            }
        }

        return false;
    }

    private static bool FoldersMatch(string? targetFolder, string? sourceFolder)
    {
        if (string.IsNullOrWhiteSpace(targetFolder) || string.IsNullOrWhiteSpace(sourceFolder))
            return true;

        return NormalizeTorrentPath(targetFolder).TrimEnd('/')
            .Equals(NormalizeTorrentPath(sourceFolder).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static TorrentFile? FindSourceFile(string targetName, IReadOnlyList<TorrentFile> sourceFiles)
    {
        var target = targetName.Replace('\\', '/');
        var targetFileName = GetTorrentFileName(target);
        var targetSubFolder = GetSubFolder(target);
        var targetDepth = target.Count(c => c == '/');

        foreach (var sourceFile in sourceFiles)
        {
            var source = sourceFile.Name.Replace('\\', '/');
            if (GetTorrentFileName(source).Equals(targetFileName, StringComparison.OrdinalIgnoreCase) &&
                GetSubFolder(source) == targetSubFolder &&
                source.Count(c => c == '/') == targetDepth)
            {
                return sourceFile;
            }
        }
        return null;
    }

    private static string GetSubFolder(string normalizedPath)
    {
        var slashIndex = normalizedPath.LastIndexOf('/');
        return slashIndex < 0 ? "" : normalizedPath[..slashIndex].TrimStart('/');
    }

    private static string NormalizeTorrentPath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static int GetTargetPathDepth(string? rootFolder, string fileName)
    {
        var normalized = fileName.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(rootFolder))
            return normalized.Count(c => c == '/');
        var root = NormalizeTorrentPath(rootFolder).TrimEnd('/');
        return normalized.StartsWith($"{root}/", StringComparison.OrdinalIgnoreCase)
            ? normalized.Count(c => c == '/')
            : normalized.Count(c => c == '/') + 1;
    }

    private static string GetTorrentFileName(string path)
    {
        var slashIndex = path.LastIndexOf('/');
        return slashIndex < 0 ? path : path[(slashIndex + 1)..];
    }

    private static bool IsTrumpableName(string name) =>
        name.Contains("-TRUMPABLE", StringComparison.OrdinalIgnoreCase);

    private static string GetTrumpableName(string originalName) =>
        IsTrumpableName(originalName)
            ? originalName
            : $"{originalName}-TRUMPABLE";

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

    /// <summary>
    /// Rehosts every image found in the description. A single image that can't be rehosted - dead,
    /// temporarily unreachable, or a failed upload - never aborts the clone and never has its original
    /// link(s) replaced with anything (a placeholder included, unless the image is CONFIRMED dead and
    /// placeholder_image is configured): the description keeps pointing at the original source for that
    /// one image, and every other image is still processed normally.
    /// </summary>
    private static readonly Regex EmptyCenterRegex = new(@"\[center\]\s*\[/center\]", RegexOptions.IgnoreCase);
    private static readonly Regex ExcessBlankLinesRegex = new(@"(\r?\n){3,}");

    /// <summary>
    /// Deleting an image block can leave an empty [center][/center] behind (a screenshot is routinely
    /// wrapped solo) and/or a run of blank lines where the block used to be. Repeated, since removing
    /// one empty [center] can occasionally expose a now-empty parent.
    /// </summary>
    private static void CleanUpAfterRemoval(StringBuilder description)
    {
        string text, prev;
        do
        {
            prev = description.ToString();
            text = EmptyCenterRegex.Replace(prev, "");
        } while (text != prev);
        text = ExcessBlankLinesRegex.Replace(text, "\n\n");
        description.Clear();
        description.Append(text);
    }

    private async Task<(int Rehosted, int Dead, int Unavailable, int DeadHostRemoved)> RehostImagesAsync(StringBuilder description)
    {
        var str = description.ToString();

        var urlWrappedImgRegex = new Regex(
            @"\[url=(?<href>[^\]]*)\]\[img\b[^\]]*\](?<img>[^\[]*)\[/img\]\[/url\]",
            RegexOptions.IgnoreCase);
        var plainImgRegex = new Regex(
            @"\[img\b[^\]]*\](?<img>[^\[]*)\[/img\]",
            RegexOptions.IgnoreCase);
        var comparisonRegex = new Regex(
            @"\[comparison[^\]]*\](?<content>.*?)\[/comparison\]",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        // Restricted to an actual image-file extension (unlike a blanket https?://\S+) so a comparison
        // block's non-image text - a link to the comparison tool's own page, a note, etc. - is never
        // mistaken for an image to rehost.
        var urlRegex = new Regex(@"https?://\S+\.(?:jpe?g|png|gif|webp|bmp)(?:\?\S*)?", RegexOptions.IgnoreCase);

        // FullMatch is the exact bbcode text this image occupies - for a plain or [url=]-wrapped [img],
        // that's the whole tag (and its wrapper); for a bare comparison-block URL, it's just the URL
        // itself. Deleting FullMatch (rather than just the inner URL) is what lets a known-dead-host
        // image be removed with nothing left behind at all, not even an empty [img][/img].
        var images = new List<(string ImgUrl, string? HrefUrl, string FullMatch)>();

        var urlWrappedMatches = urlWrappedImgRegex.Matches(str);
        foreach (Match m in urlWrappedMatches)
            images.Add((m.Groups["img"].Value, m.Groups["href"].Value, m.Value));

        var coveredRanges = urlWrappedMatches.Cast<Match>()
            .Select(m => (Start: m.Index, End: m.Index + m.Length))
            .ToList();

        foreach (Match m in plainImgRegex.Matches(str))
        {
            if (!coveredRanges.Any(r => m.Index >= r.Start && m.Index < r.End))
                images.Add((m.Groups["img"].Value, null, m.Value));
        }

        foreach (Match m in comparisonRegex.Matches(str))
            foreach (var sm in urlRegex.Matches(m.Groups["content"].Value).Select(u => u.Value))
                images.Add((sm, null, sm));

        images = [.. images.Where(i => !i.ImgUrl.Contains(config.ImageHostUrl, StringComparison.OrdinalIgnoreCase))];

        Console.WriteLine($"Found {images.Count} image(s) to rehost...");
        var rehostedCount = 0;
        var deadCount = 0;
        var unavailableCount = 0;
        var deadHostRemovedCount = 0;
        var removedAnything = false;

        // Phase 1: rehost every thumbnail/medium image. Href resolution is deferred to phase 2 so
        // the sibling-mismatch check below can see every href's outcome before anything is applied
        // to `description`.
        var prepared = new List<(string ImgUrl, string? HrefUrl, string FullMatch, RehostResult MediumResult)>();
        foreach (var (imgUrl, hrefUrl, fullMatch) in images)
        {
            if (string.IsNullOrEmpty(imgUrl)) continue;
            if (config.KnownImages.TryGetValue(imgUrl, out var knownUrl))
            {
                Console.WriteLine($"  Skipping (known): {imgUrl}\n    -> {knownUrl}");
                description.Replace(imgUrl, knownUrl);
                if (hrefUrl is not null && hrefUrl != imgUrl)
                    description.Replace(hrefUrl, knownUrl);
                rehostedCount++;
                continue;
            }

            var mediumResult = await imageRehoster.RehostAsync(imgUrl);
            if (mediumResult.Status == RehostStatus.KnownDeadHost)
            {
                deadHostRemovedCount++;
                removedAnything = true;
                ReplaceIgnoreCase(description, fullMatch, "");
                Console.WriteLine($"  Removed entirely: {imgUrl} ({mediumResult.Detail})");
                continue;
            }
            if (mediumResult.Status != RehostStatus.Success)
            {
                if (mediumResult.Status == RehostStatus.ConfirmedDead) deadCount++;
                else unavailableCount++;
                Console.WriteLine($"  Leaving as-is: {imgUrl} ({mediumResult.Detail})");
                continue;
            }
            prepared.Add((imgUrl, hrefUrl, fullMatch, mediumResult));
        }

        // Phase 2: resolve every distinct href up front (no replacements applied yet), then run the
        // sibling-mismatch safeguard across all of them together. Per the user's explicit instruction
        // (2026-10-05, seedpool 220786's second screenshot): a torrent's screenshots are near-always
        // produced by the same tool/uploader in one pass, so if some hrefs in this description resolve
        // to a genuinely bigger full-resolution file while one resolves to the exact same file as its
        // own thumbnail, that split itself is the signal something glitched on that one request - it
        // is not proof that particular image legitimately lacks a separate full version. A description
        // where EVERY href resolves to "same" (e.g. a host that doesn't do two-tier images at all) is
        // left alone - that's a uniform, not a suspicious, outcome.
        var hrefOutcomes = new Dictionary<string, (string Classification, string FullUrl)>();
        foreach (var (imgUrl, hrefUrl, _, mediumResult) in prepared)
        {
            if (hrefUrl is null || hrefUrl == imgUrl || hrefOutcomes.ContainsKey(hrefUrl))
                continue;
            if (ImageRehoster.IsTemporarilyPausedHost(hrefUrl))
                continue;   // handled directly in phase 3, never a candidate for the mismatch check

            var hrefIsImage = false;
            var hrefImageUrl = "";
            try
            {
                (hrefIsImage, hrefImageUrl) = await imageRehoster.GetImageFromHref(hrefUrl, mediumResult.SourceSizeBytes);
            }
            catch (Exception e)
            {
                Console.WriteLine($"    Could not resolve a full-resolution image for {hrefUrl}: {e.Message}");
            }

            if (!hrefIsImage)
            {
                hrefOutcomes[hrefUrl] = ("failed", "");
            }
            else if (hrefImageUrl == imgUrl)
            {
                hrefOutcomes[hrefUrl] = ("same", mediumResult.Image!.Full);
            }
            else
            {
                var fullResult = await imageRehoster.RehostAsync(hrefImageUrl);
                hrefOutcomes[hrefUrl] = fullResult.Status == RehostStatus.Success
                    ? ("bigger", fullResult.Image!.Full)
                    : ("failed", "");
            }
        }

        if (hrefOutcomes.Values.Any(v => v.Classification == "bigger"))
        {
            foreach (var hrefUrl in hrefOutcomes.Keys.ToList())
            {
                if (hrefOutcomes[hrefUrl].Classification == "same")
                {
                    Console.WriteLine($"    {hrefUrl} resolved to the same file as its thumbnail, but a sibling image in this description found a genuinely bigger one - treating this one as unresolved rather than trusting the match.");
                    hrefOutcomes[hrefUrl] = ("failed", "");
                }
            }
        }

        // Phase 3: apply every replacement now that every href's final classification is known.
        foreach (var (imgUrl, hrefUrl, _, mediumResult) in prepared)
        {
            rehostedCount++;
            var mediumImage = mediumResult.Image!;

            if (hrefUrl is null || hrefUrl == imgUrl)
            {
                description.Replace(imgUrl, mediumImage.Full);
                continue;
            }

            if (ImageRehoster.IsTemporarilyPausedHost(hrefUrl))
            {
                // The visible thumbnail (imgUrl) rehosted fine and isn't on a paused host itself - only
                // its click-through wrapper points at one (e.g. imgbox.com). Leave that wrapper's href
                // completely untouched (no fetch, no replacement) rather than resolving or repointing it,
                // same as the imgUrl-is-the-paused-host case in ImageRehoster.RehostAsync.
                Console.WriteLine($"    Click-through link {hrefUrl} is on a temporarily paused host - leaving it as-is.");
                description.Replace(imgUrl, mediumImage.Full);
                continue;
            }

            var (classification, fullUrl) = hrefOutcomes.TryGetValue(hrefUrl, out var o) ? o : ("failed", "");
            if (classification is "bigger" or "same")
            {
                ReplaceIgnoreCase(description, "[url=" + hrefUrl + "]", "[url=" + fullUrl + "]");
                description.Replace(imgUrl, mediumImage.Thumbnail);
                continue;
            }

            Console.WriteLine($"    No separate, genuinely-bigger full-resolution version found for {hrefUrl} - leaving the original click-through link as-is so the real source is still reachable.");

            // CRITICAL: never replace [url=hrefUrl] with the medium upload here. This used to
            // silently collapse the click-through link to the exact same file already shown as
            // the thumbnail, permanently erasing the only remaining path back to the real
            // external source - confirmed as a real, already-happened data-loss bug in the
            // companion Python migration tool (seedpool 144472's second image: its wrapper and
            // thumbnail ended up as the literal same uploaded file, and the original full-size
            // image's only link was gone for good). Leaving hrefUrl completely untouched means a
            // human can still click through to the real source, and a later run can still try to
            // resolve it again. The visible thumbnail still gets migrated either way.
            description.Replace(imgUrl, mediumImage.Thumbnail);
        }

        if (removedAnything)
            CleanUpAfterRemoval(description);

        return (rehostedCount, deadCount, unavailableCount, deadHostRemovedCount);
    }

    private void AppendDescriptionSuffix(StringBuilder description)
    {
        if (string.IsNullOrEmpty(config.DescriptionAppend))
            return;

        description.AppendLine();
        description.Append(config.DescriptionAppend);
    }

    private async Task SubmitEditAsync(string torrentId, string? description, string? mediaInfo, string? nameOverride)
    {
        var editPageUrl = $"{config.ToTrackerUrl}/torrents/{torrentId}/edit";
        for (var nameConflictSuffix = 0; ; nameConflictSuffix++)
        {
            Console.WriteLine($"Fetching edit page for torrent {torrentId}...");
            var editHtml = await web.GetEditPageHtmlAsync(torrentId);
            var editForm = Unit3dWebClient.ParseEditPage(editHtml);

            if (description is not null)
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
            if (!string.IsNullOrWhiteSpace(nameOverride))
            {
                var name = nameConflictSuffix == 0 ? nameOverride : nameOverride + (nameConflictSuffix + 1);
                editForm.Fields["name"] = name;
                Console.WriteLine($"Marking target torrent trumpable: {name}");
            }

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

            var redirectUrl = ToAbsolute(patchResp.Headers.Location?.ToString(), config.ToTrackerUrl);
            if (redirectUrl?.Equals(editPageUrl, StringComparison.OrdinalIgnoreCase) != true)
                return;

            var errorHtml = await web.GetPageHtmlAsync(redirectUrl);
            var errors = Unit3dWebClient.ExtractErrors(errorHtml);
            Console.WriteLine("Patch failed: tracker redirected back to edit page.");
            if (errors.Count == 0)
            {
                Console.WriteLine("  No validation error text found on edit page.");
            }
            else
            {
                foreach (var error in errors)
                    Console.WriteLine($"  {error}");
            }

            if (!string.IsNullOrWhiteSpace(nameOverride) &&
                errors.Any(error => error.Equals("The name has already been taken.", StringComparison.OrdinalIgnoreCase)))
                continue;

            throw new InvalidOperationException("Torrent edit failed.");
        }
    }

    private static StringBuilder ReplaceIgnoreCase(StringBuilder sb, string oldValue, string newValue)
    {
        var result = Regex.Replace(sb.ToString(), Regex.Escape(oldValue), _ => newValue, RegexOptions.IgnoreCase);
        sb.Clear();
        sb.Append(result);
        return sb;
    }

    private static string LayeredHtmlDecode(string formValue)
    {
        string decodedFormValue;
        while ((decodedFormValue = HttpUtility.HtmlDecode(formValue)) != formValue)
            formValue = decodedFormValue;
        return formValue;
    }

    private static string? ToAbsolute(string? url, string baseUrl)
    {
        if (url is null) return null;
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : baseUrl + (url.StartsWith('/') ? url : "/" + url);
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
