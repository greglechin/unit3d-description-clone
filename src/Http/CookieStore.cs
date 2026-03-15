using System.Net;
using Unit3dDescriptionClone.Models;
using Unit3dDescriptionClone.Serialization;

namespace Unit3dDescriptionClone.Http;

internal static class CookieStore
{
    public static CookieContainer Load(string filePath, string trackerUrl)
    {
        var container = new CookieContainer();
        if (!File.Exists(filePath))
            return container;

        Console.WriteLine("Loading cached session cookies...");
        var json = File.ReadAllText(filePath);
        var cookies = System.Text.Json.JsonSerializer.Deserialize(json, AppJsonContext.Default.ListCookieData);
        if (cookies is null)
            return container;

        var baseUri = new Uri(trackerUrl);
        foreach (var c in cookies)
        {
            var cookie = new Cookie(c.Name, c.Value, c.Path, c.Domain)
            {
                Secure = c.Secure,
                HttpOnly = c.HttpOnly,
            };
            if (c.Expires is not null)
                cookie.Expires = DateTime.Parse(c.Expires);
            container.Add(baseUri, cookie);
        }

        return container;
    }

    public static void Save(string filePath, CookieContainer container, string trackerUrl)
    {
        var cookieList = container.GetCookies(new Uri(trackerUrl))
            .Cast<Cookie>()
            .Select(c => new CookieData
            {
                Name = c.Name,
                Value = c.Value,
                Domain = c.Domain,
                Path = c.Path,
                Expires = c.Expires == DateTime.MinValue ? null : c.Expires.ToString("O"),
                Secure = c.Secure,
                HttpOnly = c.HttpOnly,
            })
            .ToList();

        var json = System.Text.Json.JsonSerializer.Serialize(cookieList, AppJsonContext.Default.ListCookieData);
        File.WriteAllText(filePath, json);
    }
}
