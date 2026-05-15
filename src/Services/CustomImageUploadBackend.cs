using System.Net.Http.Headers;
using System.Net.Http.Json;
using Unit3dDescriptionClone.Models;
using Unit3dDescriptionClone.Serialization;

namespace Unit3dDescriptionClone.Services;

internal sealed class CustomImageUploadBackend(HttpClient client, string url, string apiKey) : IImageUploadBackend
{
    public string DefaultHostDomain => "";
    public async Task<RehostedImage> UploadAsync(Stream imageStream, string fileName, string contentType)
    {
        var uploadReq = new HttpRequestMessage(HttpMethod.Post, $"{url}/upload");
        uploadReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var fileContent = new StreamContent(imageStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        uploadReq.Content = new MultipartFormDataContent
        {
            { fileContent, "files[]", fileName },
            { new StringContent("description"), "source_type" }
        };

        var resp = await client.SendAsync(uploadReq);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.UploadResponse);
        return new RehostedImage
        {
            Full = result!.Files[0].Url,
            Thumbnail = result!.Files[0].Thumbnail_url
        };
    }
}
