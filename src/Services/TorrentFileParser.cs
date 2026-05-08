using MonoTorrent.BEncoding;
using Unit3dDescriptionClone.Models;

namespace Unit3dDescriptionClone.Services;

internal static class TorrentFileParser
{
    public static List<TorrentFile> GetFiles(byte[] torrentFile)
    {
        var (torrent, _) = BEncodedDictionary.DecodeTorrent(torrentFile);
        var info = GetDictionary(torrent, "info");

        if (info.TryGetValue((BEncodedString)"files", out var filesValue))
        {
            var files = (BEncodedList)filesValue;
            return [.. files.Select(fileValue =>
            {
                var file = (BEncodedDictionary)fileValue;
                var path = (BEncodedList)file[(BEncodedString)"path"];
                return new TorrentFile
                {
                    Name = string.Join('/', path.Cast<BEncodedString>().Select(part => part.Text)),
                    Size = GetNumber(file, "length"),
                };
            })];
        }

        return
        [
            new TorrentFile
            {
                Name = GetString(info, "name"),
                Size = GetNumber(info, "length"),
            }
        ];
    }

    public static string? GetNfo(byte[] torrentFile)
    {
        var (torrent, _) = BEncodedDictionary.DecodeTorrent(torrentFile);
        var metadataDescription = TryGetNestedString(torrent, "metadata", "description")!;
        return NormalizeNfo(metadataDescription);
    }

    private static BEncodedDictionary GetDictionary(BEncodedDictionary dictionary, string key) =>
        (BEncodedDictionary)dictionary[(BEncodedString)key];

    private static long GetNumber(BEncodedDictionary dictionary, string key) =>
        ((BEncodedNumber)dictionary[(BEncodedString)key]).Number;

    private static string GetString(BEncodedDictionary dictionary, string key) =>
        ((BEncodedString)dictionary[(BEncodedString)key]).Text;

    private static string? TryGetString(BEncodedDictionary dictionary, string key) =>
        dictionary.TryGetValue((BEncodedString)key, out var value) && value is BEncodedString str
            ? str.Text
            : null;

    private static string? TryGetNestedString(BEncodedDictionary dictionary, string dictionaryKey, string stringKey) =>
        dictionary.TryGetValue((BEncodedString)dictionaryKey, out var value) && value is BEncodedDictionary nested
            ? TryGetString(nested, stringKey)
            : null;

    private static string NormalizeNfo(string value) =>
        value.Trim().Replace("[/size][/color]", "\n[/size][/color]", StringComparison.Ordinal);
}
