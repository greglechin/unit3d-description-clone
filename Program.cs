using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Http;
using Unit3dDescriptionClone.Services;

Directory.CreateDirectory("cache");

if (args.Length == 0 || (args[0] == "backfill" && args.Length < 2))
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  unit3d-description-clone <torrent-id>");
    Console.Error.WriteLine("  unit3d-description-clone backfill <release-group>");
    return 1;
}

var config = AppConfig.Load("unit3d-description-clone.ini");
Console.WriteLine($"  Source: {config.FromTrackerUrl}");
Console.WriteLine($"  Target: {config.ToTrackerUrl}");

var cookies = CookieStore.Load("cache/target-cookies.json", config.ToTrackerUrl);
using var noRedirectClient = HttpClientFactory.Create(cookies, followRedirects: false);
using var autoRedirectClient = HttpClientFactory.Create(cookies, followRedirects: true);

var api = new Unit3dApiClient(autoRedirectClient, config);
var web = new Unit3dWebClient(noRedirectClient, autoRedirectClient, cookies, config);
var imageRehoster = new ImageRehoster(autoRedirectClient, config);
var cloner = new DescriptionCloner(api, web, imageRehoster, config);

if (args[0] == "backfill")
    await cloner.BackfillAsync(args[1]);
else
    await cloner.CloneAsync(args[0]);

return 0;