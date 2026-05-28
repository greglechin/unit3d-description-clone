using System.Net.Http.Json;
using Unit3dDescriptionClone.Models;
using Unit3dDescriptionClone.Serialization;

namespace Unit3dDescriptionClone.Services;

internal sealed class PtscreensImageUploadBackend(HttpClient client, string apiKey) : IImageUploadBackend
{
    public string DefaultHostDomain => "ptscreens.com";
    private const string UploadUrl = "https://ptscreens.com/api/1/upload";

    public async Task<RehostedImage> UploadAsync(Stream imageStream, string fileName, string contentType)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, UploadUrl);
        req.Headers.Add("X-API-Key", apiKey);
        var fileContent = new StreamContent(imageStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        req.Content = new MultipartFormDataContent
        {
            { fileContent, "source", fileName }
        };

        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.PtscreensUploadResponse);
        var full = result!.Image.Image?.Url ?? result!.Image.Url;
        var thumb = result!.Image.Thumb?.Url ?? full;
        return new RehostedImage { Full = full, Thumbnail = thumb };
    }
}
