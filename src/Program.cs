using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Http;
using Unit3dDescriptionClone.Services;

Directory.CreateDirectory("cache");

var flags = args.Where(a => a.StartsWith('-')).ToHashSet(StringComparer.OrdinalIgnoreCase);
var positional = args.Where(a => !a.StartsWith('-')).ToArray();
var skipRehosting = flags.Contains("--no-rehost");
var skipAppend = flags.Contains("--no-append");

if (positional.Length == 0 || (positional[0] == "backfill" && positional.Length < 3))
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  unit3d-description-clone [--no-rehost] [--no-append] <torrent-id>");
    Console.Error.WriteLine("  unit3d-description-clone [--no-rehost] [--no-append] backfill <release-group> <uploader>");
    return 1;
}

var config = AppConfig.Load("unit3d-description-clone.ini");

var cookies = CookieStore.Load("cache/target-cookies.json", config.ToTrackerUrl);
using var noRedirectClient = HttpClientFactory.Create(cookies, followRedirects: false);
using var autoRedirectClient = HttpClientFactory.Create(cookies, followRedirects: true);

var unit3dApi = new Unit3dApiClient(autoRedirectClient, config);
var f3nixApi = new F3nixApiClient(autoRedirectClient);
var torznabApi = new TorznabApiClient(autoRedirectClient);
var web = new Unit3dWebClient(noRedirectClient, autoRedirectClient, cookies, config);
IImageUploadBackend imageUploadBackend = config.ImageHostType switch
{
    ImageHostType.Imgbb => new ImgbbImageUploadBackend(autoRedirectClient, config.ImageHostApiKey),
    ImageHostType.Ptscreens => new PtscreensImageUploadBackend(autoRedirectClient, config.ImageHostApiKey),
    _ => new CustomImageUploadBackend(autoRedirectClient, config.ImageHostUrl, config.ImageHostApiKey)
};
if (string.IsNullOrEmpty(config.ImageHostUrl))
    config = config with { ImageHostUrl = imageUploadBackend.DefaultHostDomain };
var imageRehoster = new ImageRehoster(autoRedirectClient, imageUploadBackend, config);
var cloner = new DescriptionCloner(unit3dApi, f3nixApi, torznabApi, web, imageRehoster, config);

if (positional[0] == "backfill")
    await cloner.BackfillAsync(positional[1], positional[2], skipRehosting, skipAppend);
else
    await cloner.CloneAsync(positional[0], skipRehosting, skipAppend);

return 0;
