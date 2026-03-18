using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Http;
using Unit3dDescriptionClone.Services;

Directory.CreateDirectory("cache");

var flags = args.Where(a => a.StartsWith('-')).ToHashSet(StringComparer.OrdinalIgnoreCase);
var positional = args.Where(a => !a.StartsWith('-')).ToArray();
var skipRehosting = flags.Contains("--no-rehost");

if (positional.Length == 0 || (positional[0] == "backfill" && positional.Length < 3))
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  unit3d-description-clone [--no-rehost] <torrent-id>");
    Console.Error.WriteLine("  unit3d-description-clone [--no-rehost] backfill <release-group> <uploader>");
    return 1;
}

var config = AppConfig.Load("unit3d-description-clone.ini");

var cookies = CookieStore.Load("cache/target-cookies.json", config.ToTrackerUrl);
using var noRedirectClient = HttpClientFactory.Create(cookies, followRedirects: false);
using var autoRedirectClient = HttpClientFactory.Create(cookies, followRedirects: true);

var unit3dApi = new Unit3dApiClient(autoRedirectClient, config);
var f3nixApi = new F3nixApiClient(autoRedirectClient);
var web = new Unit3dWebClient(noRedirectClient, autoRedirectClient, cookies, config);
var imageRehoster = new ImageRehoster(autoRedirectClient, config);
var cloner = new DescriptionCloner(unit3dApi, f3nixApi, web, imageRehoster, config);

if (positional[0] == "backfill")
    await cloner.BackfillAsync(positional[1], positional[2], skipRehosting);
else
    await cloner.CloneAsync(positional[0], skipRehosting);

return 0;