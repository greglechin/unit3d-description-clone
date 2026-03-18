using System.Text.Json.Serialization;
using Unit3dDescriptionClone.Models;

namespace Unit3dDescriptionClone.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<CookieData>))]
[JsonSerializable(typeof(TorrentInfo))]
[JsonSerializable(typeof(TorrentsResponse))]
[JsonSerializable(typeof(TorrentsLinks))]
[JsonSerializable(typeof(UploadResponse))]
[JsonSerializable(typeof(F3nixSearchResponse))]
[JsonSerializable(typeof(F3nixDetailsResponse))]
internal partial class AppJsonContext : JsonSerializerContext { }
