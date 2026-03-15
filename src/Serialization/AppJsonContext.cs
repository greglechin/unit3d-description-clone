using System.Text.Json.Serialization;
using Unit3dDescriptionClone.Models;

namespace Unit3dDescriptionClone.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<CookieData>))]
[JsonSerializable(typeof(TorrentInfo))]
[JsonSerializable(typeof(TorrentsResponse))]
[JsonSerializable(typeof(TorrentsLinks))]
[JsonSerializable(typeof(UploadResponse))]
internal partial class AppJsonContext : JsonSerializerContext { }
