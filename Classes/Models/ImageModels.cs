namespace Unit3dDescriptionClone.Models;

internal sealed class UploadResponse
{
    public required List<UploadFile> Files { get; set; }
}

internal sealed class UploadFile
{
    public required string Url { get; set; }
}
