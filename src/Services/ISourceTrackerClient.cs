using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Models;

namespace Unit3dDescriptionClone.Services;

internal interface ISourceTrackerClient
{
    Task<SourceTorrentResult?> FindSourceTorrentAsync(string fileName, FromTrackerConfig fromTracker);
    Task<SourceTorrentResult?> FindSourceTorrentByTmdbIdAsync(int tmdbId, string fileName, FromTrackerConfig fromTracker);
}

internal sealed record SourceTorrentResult(string Id, string Description, string? MediaInfo, IReadOnlyList<TorrentFile> Files);
