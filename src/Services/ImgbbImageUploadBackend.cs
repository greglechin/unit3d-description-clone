using System.Net.Http.Json;
using Unit3dDescriptionClone.Models;
using Unit3dDescriptionClone.Serialization;

namespace Unit3dDescriptionClone.Services;

internal sealed class ImgbbImageUploadBackend(HttpClient client, string apiKey) : IImageUploadBackend
{
    public string DefaultHostDomain => "i.ibb.co";
    private const string UploadUrl = "https://api.imgbb.com/1/upload";

    public async Task<RehostedImage> UploadAsync(Stream imageStream, string fileName, string contentType)
    {
        using var ms = new MemoryStream();
        await imageStream.CopyToAsync(ms);
        var base64 = Convert.ToBase64String(ms.ToArray());

        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("image", base64),
            new KeyValuePair<string, string>("name", Path.GetFileNameWithoutExtension(fileName))
        ]);

        var resp = await client.PostAsync($"{UploadUrl}?key={Uri.EscapeDataString(apiKey)}", form);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.ImgbbUploadResponse);
        var full = result!.Data.Url;
        var thumb = result!.Data.Thumb?.Url ?? full;
        return new RehostedImage { Full = full, Thumbnail = thumb };
    }
}
