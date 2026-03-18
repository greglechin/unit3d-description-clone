using Unit3dDescriptionClone.Config;

namespace Unit3dDescriptionClone.Services;

internal interface ISourceTrackerClient
{
    Task<SourceTorrentResult?> FindSourceTorrentAsync(string fileName, FromTrackerConfig fromTracker);
    Task<SourceTorrentResult?> FindSourceTorrentByTmdbIdAsync(int tmdbId, string fileName, FromTrackerConfig fromTracker);
}

internal sealed record SourceTorrentResult(string Description, string? MediaInfo);
