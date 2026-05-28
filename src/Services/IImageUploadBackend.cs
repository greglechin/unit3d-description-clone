using Unit3dDescriptionClone.Models;

namespace Unit3dDescriptionClone.Services;

internal interface IImageUploadBackend
{
    string DefaultHostDomain { get; }
    Task<RehostedImage> UploadAsync(Stream imageStream, string fileName, string contentType);
}
