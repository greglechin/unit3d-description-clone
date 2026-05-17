using System.Text.Json.Serialization;
using Unit3dDescriptionClone.Serialization;

namespace Unit3dDescriptionClone.Models;

internal sealed class TorrentInfo
{
    public string Id { get; set; } = "";
    public required TorrentAttributes Attributes { get; set; }
}

internal sealed class TorrentAttributes
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    [JsonPropertyName("tmdb_id")]
    [JsonConverter(typeof(NullableIntFromStringConverter))]
    public int? TmdbId { get; set; }

    [JsonPropertyName("media_info")]
    public string? MediaInfo { get; set; }

    [JsonPropertyName("download_link")]
    public string? DownloadLink { get; set; }

    public string? Folder { get; set; }

    public List<TorrentFile> Files { get; set; } = [];
}

internal sealed class TorrentFile
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
}

internal sealed class TorrentsResponse
{
    public List<TorrentInfo> Data { get; set; } = [];
    public TorrentsLinks? Links { get; set; }
}

internal sealed class TorrentsLinks
{
    public string? Next { get; set; }
}
