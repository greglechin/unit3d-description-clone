using System.Text.Json.Serialization;

namespace Unit3dDescriptionClone.Models;

internal sealed class F3nixSearchItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    [JsonPropertyName("folder_name")]
    public string FolderName { get; set; } = "";

    [JsonPropertyName("tmdb_id")]
    public string? TmdbId { get; set; }

    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; set; }
}

internal sealed class F3nixSearchResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    public List<F3nixSearchItem> Results { get; set; } = [];

    public bool Success { get; set; }
}

internal sealed class F3nixDetailItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    [JsonPropertyName("mediainfo")]
    public string? Mediainfo { get; set; }

    [JsonPropertyName("folder_name")]
    public string FolderName { get; set; } = "";

    [JsonPropertyName("tmdb_id")]
    public string? TmdbId { get; set; }

    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; set; }
}

internal sealed class F3nixDetailsResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    public F3nixDetailItem? Result { get; set; }

    public bool Success { get; set; }
}
