using System.Net;

namespace Unit3dDescriptionClone.Http;

internal static class HttpClientFactory
{
    private const string UserAgent =
        "Mozilla/5.0 (X11; Linux x86_64; rv:124.0) Gecko/20100101 Firefox/124.0";

    public static HttpClient Create(CookieContainer cookies, bool followRedirects)
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = cookies,
            AllowAutoRedirect = followRedirects,
            AutomaticDecompression = DecompressionMethods.All,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }
}
