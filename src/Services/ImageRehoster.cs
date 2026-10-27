using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Models;
using Unit3dDescriptionClone.Serialization;

namespace Unit3dDescriptionClone.Services;

/// <summary>
/// Outcome of trying to fetch a resource (an image, or a click-through page). Confirmed-gone (404/410) is
/// kept distinct from every other failure (timeout, 5xx, 429, connection error, ...): only the former is
/// real evidence the resource is actually gone. The latter just means the fetch didn't work right now, and
/// must never be treated as proof the link is dead.
/// </summary>
internal enum FetchStatus { Ok, ConfirmedDead, TemporarilyUnavailable }

/// <summary>
/// Outcome of rehosting one image. <see cref="Success"/> is the only case with a usable
/// <see cref="RehostResult.Image"/>. Every other case except <see cref="KnownDeadHost"/> means the
/// original link(s) for this image must be left exactly as they were in the description, not replaced
/// with anything (including a placeholder), so a single bad image never destroys information that was
/// still recoverable at the source. <see cref="KnownDeadHost"/> means the whole host is manually
/// confirmed gone for good (not just this one file) - the caller deletes the image's entire bbcode
/// block instead, since there is no scenario where leaving a permanently-broken link (or a placeholder
/// graphic) behind is preferable to simply not having that image there at all.
/// </summary>
internal enum RehostStatus { Success, ConfirmedDead, TemporarilyUnavailable, UploadFailed, KnownDeadHost }

internal sealed record RehostResult(RehostStatus Status, RehostedImage? Image, string? Detail, long? SourceSizeBytes = null);

internal sealed class ImageRehoster(HttpClient client, AppConfig config)
{
    private const int FetchRetries = 2;

    // Only these two statuses mean "this specific resource is confirmed gone, not just having a bad
    // moment" - see FetchStatus's own doc comment.
    private static readonly HashSet<HttpStatusCode> DefinitelyGoneStatusCodes =
        [HttpStatusCode.NotFound, HttpStatusCode.Gone];

    // Whole sites manually confirmed gone for good, not just "this one file 404s" - matched hosts skip
    // straight to RehostStatus.KnownDeadHost with no fetch attempt at all, the same way the Python
    // migration's is_known_dead_host()/dead_hosts.json manually_confirmed flag does.
    private static readonly HashSet<string> KnownDeadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "ptpimg.me", "seedimg.org",
    };

    private static bool IsKnownDeadHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && KnownDeadHosts.Contains(uri.Host);

    // Hosts paused for right now, for operational reasons only - NOT a dead-host verdict (that's
    // KnownDeadHosts above). imgbox.com is a real, ongoing outage: skip it outright, with no fetch
    // attempt at all, rather than burn retries on every single imgbox link while it's down. Matched
    // hosts are left completely untouched in the description - no placeholder, nothing replaced -
    // so the original link is still there to resolve once the host is back. Remove a host from here
    // (or empty the set) once it's confirmed back up.
    private static readonly HashSet<string> TemporarilyPausedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "imgbox.com",
    };

    // Matches any subdomain too (images2.imgbox.com, thumbs2.imgbox.com, 8-t.imgbox.com, ...).
    internal static bool IsTemporarilyPausedHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && TemporarilyPausedHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    // Ordinary web links that must never be treated as a click-through to a bigger picture, the same
    // role Python migration's NON_IMAGE_LINK_HOSTS/is_image_click_through() plays. Without this,
    // CandidateFullUrls' generic "try the href itself" / og:image fallbacks will treat ANY link as a
    // potential full-resolution candidate - confirmed real damage from exactly that gap, twice: the
    // Python tool once replaced footer links on 10 Anime torrents with github's/rust-lang's own social
    // preview images, and separately had a torrent's uploader-credit link (a Codeberg project page,
    // seedpool 121524) resolved to that page's auto-generated social-preview PNG and uploaded as if it
    // were the torrent's real cover art. Both class of mistake apply here exactly as much as there.
    private static readonly HashSet<string> NonImageLinkHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "seedbrr.com", "github.com", "rust-lang.org", "imdb.com", "themoviedb.org", "thetvdb.com",
        "myanimelist.net", "anidb.net", "steampowered.com", "youtube.com", "youtu.be", "wikipedia.org",
        "codeberg.org", "gitlab.com", "bitbucket.org", "sourceforge.net", "sr.ht",
    };

    private static bool IsNonImageLinkHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && NonImageLinkHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    // A real direct full-resolution image link essentially always ends in a recognized image
    // extension; an auto-generated page endpoint (a social-preview card, an og:image redirect, ...)
    // usually doesn't, even though it genuinely serves image/* content when fetched directly. This is
    // the pattern-based safety net (vs. NonImageLinkHosts' list-based one) - it catches a host that was
    // never specifically added to the list above. Confirmed real case it would have caught: Codeberg's
    // /-/summary-card endpoint (seedpool 121524) - no file extension at all, yet a real PNG.
    private static readonly HashSet<string> KnownImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "gif", "webp", "bmp", "avif",
    };

    private static bool HasKnownImageExtension(string url)
    {
        var path = url.Split('?')[0];
        var ext = Path.GetExtension(path).TrimStart('.');
        return KnownImageExtensions.Contains(ext);
    }

    // Per-host pacing for downloads from external image hosts, mirroring the Python migration's
    // _pace_host_request(). Confirmed real case there (seedpool 121540, 6 large ibb.co images in one
    // torrent): the first 3 downloaded fine back-to-back with no delay, the next 3 came back as
    // inconclusive/throttled responses - re-fetching the exact same URLs later, unmodified, succeeded
    // immediately. Nothing here is Python-specific; the same host would very plausibly throttle this
    // tool's requests the same way under the same conditions.
    private readonly Dictionary<string, DateTime> hostLastRequestTime = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan HostDownloadMinInterval = TimeSpan.FromSeconds(2);

    private async Task PaceHostRequestAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;
        if (hostLastRequestTime.TryGetValue(uri.Host, out var last))
        {
            var wait = HostDownloadMinInterval - (DateTime.UtcNow - last);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);
        }
        hostLastRequestTime[uri.Host] = DateTime.UtcNow;
    }

    public async Task<RehostResult> RehostAsync(string imageUrl)
    {
        if (IsTemporarilyPausedHost(imageUrl))
        {
            const string detail = "host is temporarily paused (known ongoing outage) - leaving the original link as-is, no fetch attempted";
            Console.WriteLine($"  {imageUrl}: {detail}");
            return new RehostResult(RehostStatus.TemporarilyUnavailable, null, detail);
        }

        if (IsKnownDeadHost(imageUrl))
        {
            const string detail = "host is manually confirmed dead for good - deleting this image's block from the description";
            Console.WriteLine($"  {imageUrl}: {detail}");
            return new RehostResult(RehostStatus.KnownDeadHost, null, detail);
        }

        Console.WriteLine($"  Rehosting: {imageUrl}");

        var (imageResp, fetchStatus) = await FetchWithRetryAsync(imageUrl);
        if (imageResp is null)
        {
            if (fetchStatus == FetchStatus.ConfirmedDead && !string.IsNullOrEmpty(config.ImageHostPlaceholder))
            {
                // A placeholder is only ever substituted for a CONFIRMED-dead link (never for a merely
                // temporary failure) - and only when the operator has explicitly opted into losing the
                // original URL by configuring placeholder_image at all.
                Console.WriteLine($"    Confirmed dead (404/410) - using placeholder image: {config.ImageHostPlaceholder}");
                return new RehostResult(RehostStatus.Success, new RehostedImage
                {
                    Full = config.ImageHostPlaceholder,
                    Thumbnail = config.ImageHostPlaceholder,
                }, null);
            }

            var status = fetchStatus == FetchStatus.ConfirmedDead ? RehostStatus.ConfirmedDead : RehostStatus.TemporarilyUnavailable;
            var detail = status == RehostStatus.ConfirmedDead
                ? "confirmed dead (404/410) - leaving the original link as-is"
                : "temporarily unavailable (not a confirmed 404/410) - leaving the original link as-is, may succeed on a later run";
            Console.WriteLine($"    {detail}");
            return new RehostResult(status, null, detail);
        }

        var contentType = imageResp.Content.Headers.ContentType?.MediaType ?? "";
        var rawBytes = await imageResp.Content.ReadAsByteArrayAsync();
        var fileName = Path.GetFileName(new Uri(imageUrl).LocalPath);

        var labelledSvg = contentType.Contains("svg", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
        var rasterType = GetRasterImageType(rawBytes);

        Stream uploadStream;
        if (labelledSvg && rasterType is null)
        {
            Console.WriteLine("    Converting SVG to PNG...");
            try
            {
                uploadStream = ConvertSvgToPng(new MemoryStream(rawBytes));
                fileName = Path.ChangeExtension(fileName, ".png");
                contentType = "image/png";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    SVG conversion failed ({ex.Message}); using placeholder image.");
                if (!string.IsNullOrEmpty(config.ImageHostPlaceholder))
                {
                    return new RehostedImage
                    {
                        Full = config.ImageHostPlaceholder,
                        Thumbnail = config.ImageHostPlaceholder
                    };
                }
                return null;
            }
        }
        else
        {
            if (labelledSvg)
                Console.WriteLine($"    Labelled as SVG but content is {rasterType}; uploading as-is.");
            if (rasterType is not null)
                contentType = rasterType;
            uploadStream = new MemoryStream(rawBytes);
        }

        using var uploadBuffer = new MemoryStream();
        await uploadStream.CopyToAsync(uploadBuffer);
        var uploadBytes = uploadBuffer.ToArray();

        var resp = await SendWithRetryAsync(() =>
        {
            var uploadReq = new HttpRequestMessage(HttpMethod.Post, $"{config.ImageHostUrl}/upload");
            uploadReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ImageHostApiKey);
            var fileContent = new StreamContent(new MemoryStream(uploadBytes));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            uploadReq.Content = new MultipartFormDataContent
            {
                { fileContent, "files[]", fileName },
                { new StringContent("description"), "source_type" },
            };
            return uploadReq;
        });
        if (resp is null)
        {
            var detail = "upload to the image host failed after retries - leaving the original link as-is, may succeed on a later run";
            Console.WriteLine($"    {detail}");
            return new RehostResult(RehostStatus.UploadFailed, null, detail);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var detail = $"upload to the image host returned {(int)resp.StatusCode} - leaving the original link as-is";
                Console.WriteLine($"    {detail}");
                return new RehostResult(RehostStatus.UploadFailed, null, detail);
            }

            var result = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.UploadResponse);
            var newFullUrl = result!.Files[0].Url;
            var newThumbnailUrl = result!.Files[0].Thumbnail_url;
            Console.WriteLine($"    -> {newFullUrl}");
            Console.WriteLine($"    -> {newThumbnailUrl}");
            return new RehostResult(RehostStatus.Success, new RehostedImage
            {
                Full = newFullUrl,
                Thumbnail = newThumbnailUrl,
            }, null, uploadBytes.LongLength);
        }
    }

    private async Task<HttpResponseMessage?> SendWithRetryAsync(Func<HttpRequestMessage> buildRequest)
    {
        for (var attempt = 0; attempt <= FetchRetries; attempt++)
        {
            try
            {
                using var req = buildRequest();
                var resp = await client.SendAsync(req);
                if ((resp.StatusCode != HttpStatusCode.RequestTimeout
                        && resp.StatusCode != HttpStatusCode.TooManyRequests
                        && (int)resp.StatusCode < 500)
                    || attempt == FetchRetries)
                    return resp;

                Console.WriteLine($"    Upload failed ({(int)resp.StatusCode}), retrying ({attempt + 1}/{FetchRetries})...");
                resp.Dispose();
                await Task.Delay(1000);
            }
            catch (Exception) when (attempt < FetchRetries)
            {
                Console.WriteLine($"    Upload failed, retrying ({attempt + 1}/{FetchRetries})...");
                await Task.Delay(1000);
            }
            catch (Exception)
            {
                Console.WriteLine($"    Failed to upload image after {FetchRetries + 1} attempts, aborting.");
                return null;
            }
        }

        Console.WriteLine($"    Failed to upload image after {FetchRetries + 1} attempts, aborting.");
        return null;
    }

    // Click-through resolvers, tried in order until one produces something that actually downloads and is
    // genuinely bigger than the embedded thumbnail. Each one independent of the others, so a wrong guess
    // (or a host this doesn't know) falls through to the next instead of silently giving up on the whole
    // idea of a full-resolution image.
    private static readonly (string Suffix, string Replacement)[] ThumbnailSuffixes =
    [
        (".md.", "."), (".th.", "."),
    ];

    /// <summary>
    /// Resolves a click-through href to the true full-resolution image, verifying it against the size of
    /// the thumbnail already embedded in the description so a same-size or smaller "full" (a wrong guess,
    /// or a host that doesn't actually have a bigger version) is never silently accepted as real.
    /// Returns (false, "") if nothing usable was found, or if hrefUrl is a known ordinary (non-image)
    /// link - the caller then leaves the original click-through link completely untouched rather than
    /// losing it to a bad "full" guess (see the critical note at this method's call site).
    /// </summary>
    public async Task<(bool IsImage, string ImageUrl)> GetImageFromHref(string hrefUrl, long? thumbnailSizeBytes)
    {
        if (IsNonImageLinkHost(hrefUrl))
        {
            Console.WriteLine($"    {hrefUrl} is a known ordinary (non-image) link host - never treated as a click-through.");
            return (false, "");
        }

        await foreach (var candidate in CandidateFullUrls(hrefUrl))
        {
            byte[]? bytes;
            try
            {
                bytes = await DownloadIfImageAsync(candidate);
            }
            catch (Exception e)
            {
                Console.WriteLine($"    Full-resolution candidate {candidate} failed: {e.Message}");
                continue;
            }
            if (bytes is null)
                continue;

            // A candidate that is STRICTLY smaller than the thumbnail is a worse derivative and gets
            // skipped, same as before. One that is EXACTLY the same size is not a worse guess to reject,
            // though - it's confirmation (not a guess) that this host has no separate, bigger original,
            // so the medium already IS the full-resolution file. Per the user's explicit instruction
            // (2026-10-04, seedpool 1029661/ptscreens.com): accept it here rather than falling through to
            // "nothing found", so the caller can safely reuse the thumbnail's own CDN link instead of
            // leaving the original external click-through link in place.
            if (thumbnailSizeBytes is { } known && bytes.Length < known)
            {
                Console.WriteLine($"    Full-resolution candidate {candidate} is smaller than the thumbnail ({bytes.Length} vs {known} bytes) - skipping it.");
                continue;
            }

            if (!HasKnownImageExtension(candidate))
            {
                Console.WriteLine($"    Full-resolution candidate {candidate} has no recognized image file extension despite an image/* response - likely an auto-generated page preview rather than the real file, skipping it.");
                continue;
            }

            return (true, candidate);
        }

        return (false, "");
    }

    /// <summary>
    /// Every way of guessing the full-resolution address, cheapest/most specific first. A guess here is
    /// only ever a candidate URL to try - <see cref="GetImageFromHref"/> is what actually verifies it.
    /// </summary>
    private async IAsyncEnumerable<string> CandidateFullUrls(string hrefUrl)
    {
        // Chevereto-style hosts (beyondhd.co, img4k.net, onlyimage.org, ...): "<hash>.md.<ext>" /
        // "<hash>.th.<ext>" sit next to the true original "<hash>.<ext>" at the same path.
        foreach (var (suffix, replacement) in ThumbnailSuffixes)
        {
            var idx = hrefUrl.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                yield return hrefUrl.Remove(idx, suffix.Length).Insert(idx, replacement);
        }

        // pixhost.to: "tN.pixhost.to/thumbs/<name>" (thumbnail) -> "imgN.pixhost.to/images/<name>" (original).
        var pixhostMatch = System.Text.RegularExpressions.Regex.Match(
            hrefUrl, @"^(https?://)t(\d+)\.pixhost\.to/thumbs/(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (pixhostMatch.Success)
            yield return $"{pixhostMatch.Groups[1].Value}img{pixhostMatch.Groups[2].Value}.pixhost.to/images/{pixhostMatch.Groups[3].Value}";

        // imgbox: the page's own ".image-content" <img> src is the full-resolution file.
        if (hrefUrl.Contains("imgbox", StringComparison.OrdinalIgnoreCase))
        {
            var scraped = await TryScrapePageAsync(hrefUrl,
                "//img[contains(concat(' ', normalize-space(@class), ' '), ' image-content ')]", "src");
            if (scraped is not null)
                yield return scraped;
        }

        // The href may already BE the full-resolution file directly (a direct i.ibb.co link, a
        // beyondhd.co /cache/i/ path, etc.) - GetImageFromHref's own download-and-size-check covers this,
        // so it is tried as a candidate in its own right too.
        yield return hrefUrl;

        // Generic fallback for any other gallery/host: the page's own og:image meta tag. This is what the
        // original ibb.co/beyondhd.co-specific branches did - generalized here to any host, since nothing
        // about reading a page's og:image is actually host-specific.
        var ogImage = await TryScrapePageAsync(hrefUrl, "//meta[@property='og:image']", "content");
        if (ogImage is not null)
            yield return ogImage;
    }

    private async Task<string?> TryScrapePageAsync(string pageUrl, string xpath, string attribute)
    {
        HttpResponseMessage resp;
        try
        {
            await PaceHostRequestAsync(pageUrl);
            resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, pageUrl));
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception e)
        {
            Console.WriteLine($"    Could not fetch {pageUrl} to look for a full-resolution image: {e.Message}");
            return null;
        }
        using (resp)
        {
            var content = await resp.Content.ReadAsStringAsync();
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(content);
            var node = doc.DocumentNode.SelectSingleNode(xpath);
            return node?.GetAttributeValue(attribute, null);
        }
    }

    private async Task<byte[]?> DownloadIfImageAsync(string url)
    {
        var (resp, status) = await FetchWithRetryAsync(url);
        if (resp is null || status != FetchStatus.Ok)
            return null;

        using (resp)
        {
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;
            return await resp.Content.ReadAsByteArrayAsync();
        }
    }

    private async Task<(HttpResponseMessage? Response, FetchStatus Status)> FetchWithRetryAsync(string imageUrl)
    {
        HttpStatusCode? lastStatusCode = null;
        for (var attempt = 0; attempt <= FetchRetries; attempt++)
        {
            try
            {
                await PaceHostRequestAsync(imageUrl);
                var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, imageUrl));
                lastStatusCode = resp.StatusCode;
                if (resp.IsSuccessStatusCode && resp.Content is not null)
                {
                    // A "soft 404": the server redirects to a static error graphic with an honest
                    // HTTP 200 on the final hop, instead of ever returning a real 4xx/5xx status.
                    // Confirmed real case (2026-10-05, seedpool 225891): a.l3n.co/b.l3n.co/c.l3n.co
                    // all redirect a deleted image's URL to https://cdn.lensdump.com/404.png (200 OK,
                    // same file every time). Checking the final URL's own filename after following
                    // every redirect catches this regardless of host, and also avoids uploading the
                    // same dead placeholder as a "new" CDN file every time it's hit.
                    var finalPath = resp.RequestMessage?.RequestUri?.AbsolutePath ?? "";
                    if (System.Text.RegularExpressions.Regex.IsMatch(
                            finalPath, @"/404\.(png|jpe?g|gif|webp|bmp)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        resp.Dispose();
                        return (null, FetchStatus.ConfirmedDead);
                    }
                    return (resp, FetchStatus.Ok);
                }

                if (DefinitelyGoneStatusCodes.Contains(resp.StatusCode))
                {
                    resp.Dispose();
                    return (null, FetchStatus.ConfirmedDead);
                }

                resp.Dispose();
                if (attempt < FetchRetries)
                {
                    Console.WriteLine($"    HTTP {(int)lastStatusCode} fetching image, retrying ({attempt + 1}/{FetchRetries})...");
                    await Task.Delay(1000);
                    continue;
                }
            }
            catch (Exception) when (attempt < FetchRetries)
            {
                Console.WriteLine($"    Timeout fetching image, retrying ({attempt + 1}/{FetchRetries})...");
                await Task.Delay(1000);
            }
            catch (Exception)
            {
                Console.WriteLine($"    Failed to fetch image after {FetchRetries + 1} attempts.");
                return (null, FetchStatus.TemporarilyUnavailable);
            }
        }

        Console.WriteLine($"    Failed to fetch image after {FetchRetries + 1} attempts.");
        return (null, FetchStatus.TemporarilyUnavailable);
    }

    private static string? GetRasterImageType(byte[] bytes)
    {
        var b = bytes.AsSpan();
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            return "image/png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8)
            return "image/jpeg";
        if (b.Length > 12 && b.Slice(0, 4).SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";
        if (b.Length > 6 && (b.Slice(0, 6).SequenceEqual("GIF87a"u8) || b.Slice(0, 6).SequenceEqual("GIF89a"u8)))
            return "image/gif";
        return null;
    }

    private static Stream ConvertSvgToPng(Stream svgStream)
    {
        var svgDoc = new Svg.Skia.SKSvg();
        svgDoc.Load(svgStream);
        var bounds = svgDoc.Picture!.CullRect;
        using var bitmap = new SkiaSharp.SKBitmap((int)bounds.Width, (int)bounds.Height);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.Transparent);
        canvas.DrawPicture(svgDoc.Picture);
        canvas.Flush();
        var pngData = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var stream = pngData.AsStream();
        stream.Position = 0;
        return stream;
    }
}
