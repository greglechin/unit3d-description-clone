namespace Unit3dDescriptionClone.Config;

using System.Text.RegularExpressions;

internal enum TrackerType { UNIT3D, F3NIX, TORZNAB }

internal enum ImageHostType { Custom, Imgbb, Ptscreens }

internal sealed record FetchCookiesConfig(string Url, IReadOnlyList<string> Cookies);

internal sealed record FromTrackerConfig(
    TrackerType TrackerType,
    string Url,
    string ApiKey,
    string RssKey,
    bool GrabNfoFromTorrentFile,
    bool SupportsFileNameSearch,
    IReadOnlyList<string> ReleaseGroups);

internal sealed record AppConfig(
    IReadOnlyList<FromTrackerConfig> FromTrackers,
    string ToTrackerUrl,
    string ToTrackerApiKey,
    string ToTrackerUsername,
    string ToTrackerPassword,
    string ToTrackerTotpSecret,
    ImageHostType ImageHostType,
    string ImageHostUrl,
    string ImageHostApiKey,
    string ImageHostPlaceholder,
    IReadOnlyDictionary<string, string> KnownImages,
    IReadOnlyList<Regex> StripLinePatterns,
    string DescriptionAppend,
    IReadOnlyList<FetchCookiesConfig> FetchCookies)
{
    public FromTrackerConfig? GetFromTrackerForTorrent(string torrentName) =>
        FromTrackers.FirstOrDefault(ft =>
            ft.ReleaseGroups.Any(rg => torrentName.EndsWith(rg, StringComparison.OrdinalIgnoreCase)));

    public static AppConfig Load(string path)
    {
        var cfg = IniConfig.Load(path);
        var to = cfg["to_tracker"][0];
        var img = cfg["image_host"][0];
        var knownImages = cfg.TryGetValue("known_images", out var ki)
            ? (IReadOnlyDictionary<string, string>)ki[0]
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        List<Regex> stripLinePatterns = cfg.TryGetValue("strip_lines", out var sl)
            && sl[0].TryGetValue("pattern", out var patterns)
            ? [.. patterns.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))]
            : [];

        List<FromTrackerConfig> fromTrackers = cfg.TryGetValue("from_tracker", out var fromSections)
            ? [.. fromSections.Select(from => new FromTrackerConfig(
                TrackerType: from.TryGetValue("type", out var typeStr)
                    ? ParseTrackerType(typeStr)
                    : TrackerType.UNIT3D,
                Url: from["url"],
                ApiKey: from["api_key"],
                RssKey: from.GetValueOrDefault("rss_key", ""),
                GrabNfoFromTorrentFile: from.TryGetValue("grab_nfo_from_torrent_file", out var gnftf)
                    && gnftf.Equals("true", StringComparison.OrdinalIgnoreCase),
                SupportsFileNameSearch: !from.TryGetValue("supports_file_name_search", out var sfns)
                    || sfns.Equals("true", StringComparison.OrdinalIgnoreCase),
                ReleaseGroups: from.TryGetValue("release_group", out var rg)
                    ? rg.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : []))]
            : [];

        string descriptionAppend = cfg.TryGetValue("description_append", out var da)
            && da[0].TryGetValue("_content", out var daContent)
            ? daContent.TrimEnd()
            : "";

        List<FetchCookiesConfig> fetchCookies = cfg.TryGetValue("fetch_cookies", out var fcSections)
            ? [.. fcSections.Select(fc => new FetchCookiesConfig(
                Url: fc["url"],
                Cookies: fc.TryGetValue("cookie", out var cookieStr)
                    ? cookieStr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : []))]
            : [];

        var imageHostType = img.TryGetValue("type", out var hostTypeStr)
            ? ParseImageHostType(hostTypeStr)
            : ImageHostType.Custom;

        return new AppConfig(
            FromTrackers: fromTrackers,
            ToTrackerUrl: to["url"],
            ToTrackerApiKey: to["api_key"],
            ToTrackerUsername: to["username"],
            ToTrackerPassword: to["password"],
            ToTrackerTotpSecret: to.GetValueOrDefault("totp_secret", ""),
            ImageHostType: imageHostType,
            ImageHostUrl: img.GetValueOrDefault("url", ""),
            ImageHostApiKey: img["api_key"],
            ImageHostPlaceholder: img.GetValueOrDefault("placeholder_image", ""),
            KnownImages: knownImages,
            StripLinePatterns: stripLinePatterns,
            DescriptionAppend: descriptionAppend,
            FetchCookies: fetchCookies);
    }

    private static TrackerType ParseTrackerType(string value) =>
        value.Equals("F3NIX", StringComparison.OrdinalIgnoreCase)
            ? TrackerType.F3NIX
            : value.Equals("TORZNAB", StringComparison.OrdinalIgnoreCase)
                ? TrackerType.TORZNAB
                : TrackerType.UNIT3D;

    private static ImageHostType ParseImageHostType(string value) =>
        value.Equals("imgbb", StringComparison.OrdinalIgnoreCase) ? ImageHostType.Imgbb
        : value.Equals("ptscreens", StringComparison.OrdinalIgnoreCase) ? ImageHostType.Ptscreens
        : ImageHostType.Custom;
}
