namespace Unit3dDescriptionClone.Models;

internal sealed class UploadResponse
{
    public required List<UploadFile> Files { get; set; }
}

internal sealed class UploadFile
{
    public required string Url { get; set; }
    public required string Thumbnail_url { get; set; }
}

internal sealed class ImgbbUploadResponse
{
    public required ImgbbData Data { get; set; }
    public required bool Success { get; set; }
}

internal sealed class ImgbbData
{
    public required string Url { get; set; }
    public ImgbbThumb? Thumb { get; set; }
}

internal sealed class ImgbbThumb
{
    public required string Url { get; set; }
}

internal sealed class PtscreensUploadResponse
{
    public required PtscreensImageData Image { get; set; }
}

internal sealed class PtscreensImageData
{
    public required string Url { get; set; }
    public PtscreensFile? Thumb { get; set; }
    public PtscreensFile? Image { get; set; }
}

internal sealed class PtscreensFile
{
    public string? Url { get; set; }
}
