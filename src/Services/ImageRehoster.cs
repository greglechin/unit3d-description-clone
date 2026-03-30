using System.Net.Http.Headers;
using System.Net.Http.Json;
using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Serialization;

namespace Unit3dDescriptionClone.Services;

internal sealed class ImageRehoster(HttpClient client, AppConfig config)
{
    private const int FetchRetries = 2;

    public async Task<string?> RehostAsync(string imageUrl)
    {
        Console.WriteLine($"  Rehosting: {imageUrl}");

        var imageResp = await FetchWithRetryAsync(imageUrl);
        if (imageResp is null)
        {
            if (!string.IsNullOrEmpty(config.ImageHostPlaceholder))
            {
                Console.WriteLine($"    Using placeholder image: {config.ImageHostPlaceholder}");
                return config.ImageHostPlaceholder;
            }
            return null;
        }

        var contentType = imageResp.Content.Headers.ContentType?.MediaType ?? "";
        var rawStream = await imageResp.Content.ReadAsStreamAsync();
        var fileName = Path.GetFileName(new Uri(imageUrl).LocalPath);

        Stream uploadStream;
        if (contentType.Contains("svg", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("    Converting SVG to PNG...");
            uploadStream = ConvertSvgToPng(rawStream);
            fileName = Path.ChangeExtension(fileName, ".png");
            contentType = "image/png";
        }
        else
        {
            uploadStream = rawStream;
        }

        var uploadReq = new HttpRequestMessage(HttpMethod.Post, $"{config.ImageHostUrl}/upload");
        uploadReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ImageHostApiKey);
        var fileContent = new StreamContent(uploadStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        uploadReq.Content = new MultipartFormDataContent
        {
            { fileContent, "files[]", fileName },
            { new StringContent("user"), "source_type" }
        };

        var resp = await client.SendAsync(uploadReq);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.UploadResponse);
        var newUrl = result!.Files[0].Url;
        Console.WriteLine($"    -> {newUrl}");
        return newUrl;
    }

    public async Task<(bool IsImage, string ImageUrl)> GetImageFromHref(string imageUrl)
    {
        if (imageUrl.Contains("imgbox", StringComparison.OrdinalIgnoreCase))
        {
            var resp = await FetchWithRetryAsync(imageUrl);
            var content = await resp!.Content.ReadAsStringAsync();
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(content);
            var img = doc.DocumentNode.SelectSingleNode("//img[contains(concat(' ', normalize-space(@class), ' '), ' image-content ')]");
            var src = img?.GetAttributeValue("src", null);
            return (src is not null, src ?? "");
        }
        else
        {
            var type = await FetchTypeWithRetryAsync(imageUrl);
            return (type == "image", type == "image" ? imageUrl : "");
        }
    }

    private async Task<HttpResponseMessage?> FetchWithRetryAsync(string imageUrl)
    {
        for (var attempt = 0; attempt <= FetchRetries; attempt++)
        {
            try
            {
                var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, imageUrl));
                resp.EnsureSuccessStatusCode();
                return resp;
            }
            catch (TaskCanceledException) when (attempt < FetchRetries)
            {
                Console.WriteLine($"    Timeout fetching image, retrying ({attempt + 1}/{FetchRetries})...");
                await Task.Delay(1000);
            }
            catch (Exception)
            {
                Console.WriteLine($"    Failed to fetch image after {FetchRetries + 1} attempts, aborting.");
                return null;
            }
        }
        return null;
    }

    private async Task<String?> FetchTypeWithRetryAsync(string imageUrl)
    {
        for (var attempt = 0; attempt <= FetchRetries; attempt++)
        {
            try
            {
                var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, imageUrl));
                resp.EnsureSuccessStatusCode();

                var contentType = resp.Content.Headers.GetValues($"Content-Type").First();
                if (contentType is null)
                    return null;

                return contentType.Split('/').First();
            }
            catch (TaskCanceledException) when (attempt < FetchRetries)
            {
                Console.WriteLine($"    Timeout fetching type, retrying ({attempt + 1}/{FetchRetries})...");
                await Task.Delay(1000);
            }
            catch (Exception)
            {
                Console.WriteLine($"    Failed to fetch type after {FetchRetries + 1} attempts, aborting.");
                return null;
            }
        }
        return null;
    }

    private static Stream ConvertSvgToPng(Stream svgStream)
    {
        var svgDoc = new Svg.Skia.SKSvg();
        svgDoc.Load(svgStream);
        var bounds = svgDoc.Picture!.CullRect;
        using var bitmap = new SkiaSharp.SKBitmap((int)bounds.Width, (int)bounds.Height);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.Transparent);
        canvas.DrawPicture(svgDoc.Picture);
        canvas.Flush();
        var pngData = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var stream = pngData.AsStream();
        stream.Position = 0;
        return stream;
    }
}
