namespace Unit3dDescriptionClone.Config;

using System.Text.RegularExpressions;

internal enum TrackerType { UNIT3D, F3NIX }

internal sealed record FromTrackerConfig(
    TrackerType TrackerType,
    string Url,
    string ApiKey,
    bool SupportsFileNameSearch,
    IReadOnlyList<string> ReleaseGroups);

internal sealed record AppConfig(
    IReadOnlyList<FromTrackerConfig> FromTrackers,
    string ToTrackerUrl,
    string ToTrackerApiKey,
    string ToTrackerUsername,
    string ToTrackerPassword,
    string ToTrackerTotpSecret,
    string ImageHostUrl,
    string ImageHostApiKey,
    string ImageHostPlaceholder,
    IReadOnlyDictionary<string, string> KnownImages,
    IReadOnlyList<Regex> StripLinePatterns,
    string DescriptionAppend)
{
    public FromTrackerConfig? GetFromTrackerForTorrent(string torrentName) =>
        FromTrackers.FirstOrDefault(ft =>
            ft.ReleaseGroups.Any(rg => torrentName.Contains(rg, StringComparison.OrdinalIgnoreCase)));

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
                TrackerType: from.TryGetValue("type", out var typeStr) && typeStr.Equals("F3NIX", StringComparison.OrdinalIgnoreCase)
                    ? TrackerType.F3NIX
                    : TrackerType.UNIT3D,
                Url: from["url"],
                ApiKey: from["api_key"],
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

        return new AppConfig(
            FromTrackers: fromTrackers,
            ToTrackerUrl: to["url"],
            ToTrackerApiKey: to["api_key"],
            ToTrackerUsername: to["username"],
            ToTrackerPassword: to["password"],
            ToTrackerTotpSecret: to.GetValueOrDefault("totp_secret", ""),
            ImageHostUrl: img["url"],
            ImageHostApiKey: img["api_key"],
            ImageHostPlaceholder: img.GetValueOrDefault("placeholder_image", ""),
            KnownImages: knownImages,
            StripLinePatterns: stripLinePatterns,
            DescriptionAppend: descriptionAppend);
    }
}
