using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Http;
using Unit3dDescriptionClone.Services;

Directory.CreateDirectory("cache");

var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var positional = new List<string>();
string? fromTorrentId = null;
string? fromTrackerName = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i].Equals("--from-id", StringComparison.OrdinalIgnoreCase))
    {
        if (++i >= args.Length || args[i].StartsWith('-'))
        {
            Console.Error.WriteLine("--from-id requires a value.");
            return 1;
        }
        fromTorrentId = args[i];
    }
    else if (args[i].StartsWith("--from-id=", StringComparison.OrdinalIgnoreCase))
    {
        fromTorrentId = args[i][(args[i].IndexOf('=') + 1)..];
        if (string.IsNullOrWhiteSpace(fromTorrentId))
        {
            Console.Error.WriteLine("--from-id requires a value.");
            return 1;
        }
    }
    else if (args[i].StartsWith('-'))
        flags.Add(args[i]);
    else
        positional.Add(args[i]);
}

if (fromTorrentId is not null)
{
    var separatorIndex = fromTorrentId.IndexOf('/');
    if (separatorIndex <= 0 || separatorIndex == fromTorrentId.Length - 1 || fromTorrentId.IndexOf('/', separatorIndex + 1) >= 0)
    {
        Console.Error.WriteLine("--from-id requires a value in the format <from-tracker>/<id>, for example aither/12345.");
        return 1;
    }
    fromTrackerName = fromTorrentId[..separatorIndex];
    fromTorrentId = fromTorrentId[(separatorIndex + 1)..];
}

var skipRehosting = flags.Contains("--no-rehost");
var skipAppend = flags.Contains("--no-append");
var allowRerun = flags.Contains("--allow-rerun");

if (positional.Count == 0 || (positional[0] == "backfill" && positional.Count < 3))
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  unit3d-description-clone [--no-rehost] [--no-append] [--allow-rerun] [--from-id <from-tracker>/<id>] <torrent-id>");
    Console.Error.WriteLine("  unit3d-description-clone [--no-rehost] [--no-append] [--allow-rerun] backfill <release-group> <uploader>");
    return 1;
}
if (positional[0] == "backfill" && fromTorrentId is not null)
{
    Console.Error.WriteLine("--from-id cannot be used with backfill.");
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
var imageRehoster = new ImageRehoster(autoRedirectClient, config);
var cloner = new DescriptionCloner(unit3dApi, f3nixApi, torznabApi, web, imageRehoster, config);

if (positional[0] == "backfill")
    await cloner.BackfillAsync(positional[1], positional[2], skipRehosting, skipAppend, allowRerun);
else
    await cloner.CloneAsync(positional[0], skipRehosting, skipAppend, allowRerun, fromTrackerName, fromTorrentId);

return 0;
