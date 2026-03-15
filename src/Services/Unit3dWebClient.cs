using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using OtpNet;
using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Http;

namespace Unit3dDescriptionClone.Services;

internal sealed class Unit3dWebClient(
    HttpClient noRedirectClient,
    HttpClient autoRedirectClient,
    CookieContainer cookies,
    AppConfig config)
{
    private const string CookieCachePath = "cache/target-cookies.json";

    public async Task EnsureLoggedInAsync()
    {
        var testResp = await noRedirectClient.GetAsync(
            $"{config.ToTrackerUrl}/users/{config.ToTrackerUsername}/wishes");

        if (testResp.StatusCode != HttpStatusCode.Found)
        {
            Console.WriteLine("Already logged in (cached cookies valid).");
            return;
        }

        Console.WriteLine("Not logged in, authenticating...");
        await LoginAsync();
        CookieStore.Save(CookieCachePath, cookies, config.ToTrackerUrl);
    }

    public async Task<string> GetEditPageHtmlAsync(string torrentId)
        => await autoRedirectClient.GetStringAsync($"{config.ToTrackerUrl}/torrents/{torrentId}/edit");

    public async Task<HttpResponseMessage> SubmitEditFormAsync(
        string torrentId,
        string editPageUrl,
        IEnumerable<KeyValuePair<string, string>> fields)
        => await PostFormAsync(
            noRedirectClient,
            $"{config.ToTrackerUrl}/torrents/{torrentId}",
            editPageUrl,
            fields);

    public static EditFormData ParseEditPage(string html) => new(
        Csrf: ExtractCsrf(html),
        Captcha: ExtractCaptcha(html),
        Fields: ExtractFormFields(html),
        AlpineExists: ParseAlpineExists(html));

    private async Task LoginAsync()
    {
        var loginHtml = await autoRedirectClient.GetStringAsync($"{config.ToTrackerUrl}/login");
        var csrf = ExtractCsrf(loginHtml)!;
        var captcha = ExtractCaptcha(loginHtml);
        var loginFields = BuildForm(csrf, captcha,
            ("username", config.ToTrackerUsername),
            ("password", config.ToTrackerPassword),
            ("remember", "1"));

        using var loginResp = await PostFormAsync(
            noRedirectClient,
            $"{config.ToTrackerUrl}/login",
            $"{config.ToTrackerUrl}/login",
            loginFields);

        var redirectUrl = ToAbsolute(loginResp.Headers.Location?.ToString(), config.ToTrackerUrl);
        if (redirectUrl?.Contains("/two-factor-challenge") == true)
        {
            Console.WriteLine("Two-factor challenge required, submitting TOTP code...");
            await CompleteTotpChallengeAsync();
        }
        else
        {
            Console.WriteLine("Login successful.");
            await autoRedirectClient.GetAsync(redirectUrl ?? $"{config.ToTrackerUrl}/");
        }
    }

    private async Task CompleteTotpChallengeAsync()
    {
        var totpCode = GenerateTotp(config.ToTrackerTotpSecret);
        var challengeHtml = await autoRedirectClient.GetStringAsync(
            $"{config.ToTrackerUrl}/two-factor-challenge");
        var csrf = ExtractCsrf(challengeHtml)!;
        var captcha = ExtractCaptcha(challengeHtml);
        var totpFields = BuildForm(csrf, captcha, ("code", totpCode));

        using var totpResp = await PostFormAsync(
            noRedirectClient,
            $"{config.ToTrackerUrl}/two-factor-challenge",
            $"{config.ToTrackerUrl}/two-factor-challenge",
            totpFields);

        var redirect = ToAbsolute(totpResp.Headers.Location?.ToString(), config.ToTrackerUrl);
        await autoRedirectClient.GetAsync(redirect!);
    }

    private static List<KeyValuePair<string, string>> BuildForm(
        string csrf, CaptchaFields? captcha,
        params (string name, string value)[] extra)
    {
        var form = new List<KeyValuePair<string, string>> { new("_token", csrf) };
        foreach (var (name, value) in extra)
            form.Add(new(name, value));
        if (captcha is not null)
        {
            form.Add(new("_captcha", captcha.Token));
            form.Add(new("_username", ""));
            form.Add(new(captcha.RandomFieldName, captcha.RandomFieldValue));
        }
        return form;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string referer,
        IEnumerable<KeyValuePair<string, string>> fields)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        req.Headers.Referrer = new Uri(referer);
        req.Headers.Add("Origin", new Uri(url).GetLeftPart(UriPartial.Authority));
        return await client.SendAsync(req);
    }

    private static string? ExtractCsrf(string html)
    {
        var doc = ParseHtml(html);
        var tokenInput = doc.DocumentNode.Descendants("input")
            .FirstOrDefault(n => n.GetAttributeValue("name", "") == "_token");
        if (tokenInput is not null)
            return tokenInput.GetAttributeValue("value", null!);

        var csrfMeta = doc.DocumentNode.Descendants("meta")
            .FirstOrDefault(n => n.GetAttributeValue("name", "") == "csrf-token");
        return csrfMeta?.GetAttributeValue("content", null!);
    }

    private static CaptchaFields? ExtractCaptcha(string html)
    {
        var doc = ParseHtml(html);
        var captchaInput = doc.DocumentNode.Descendants("input")
            .FirstOrDefault(n => n.GetAttributeValue("name", "") == "_captcha");
        if (captchaInput is null) return null;

        var token = captchaInput.GetAttributeValue("value", "");
        var randInput = doc.DocumentNode.Descendants("input")
            .FirstOrDefault(n =>
                Regex.IsMatch(n.GetAttributeValue("name", ""), @"^[a-zA-Z0-9]{16}$") &&
                Regex.IsMatch(n.GetAttributeValue("value", ""), @"^\d{10}$"));
        if (randInput is null) return null;

        return new CaptchaFields(
            token,
            randInput.GetAttributeValue("name", ""),
            randInput.GetAttributeValue("value", ""));
    }

    private static Dictionary<string, string> ExtractFormFields(string html)
    {
        var doc = ParseHtml(html);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in doc.DocumentNode.Descendants("input"))
        {
            var name = input.GetAttributeValue("name", "");
            if (name.Length == 0) continue;
            var type = input.GetAttributeValue("type", "text").ToLowerInvariant();
            if (type is "file" or "submit") continue;
            if (type is "radio" or "checkbox" && input.Attributes["checked"] is null) continue;
            fields[name] = input.GetAttributeValue("value", "");
        }

        foreach (var textarea in doc.DocumentNode.Descendants("textarea"))
        {
            var name = textarea.GetAttributeValue("name", "");
            if (name.Length > 0)
                fields[name] = textarea.InnerText;
        }

        foreach (var select in doc.DocumentNode.Descendants("select"))
        {
            var name = select.GetAttributeValue("name", "");
            if (name.Length == 0) continue;
            var selected = select.Descendants("option")
                .FirstOrDefault(o => o.Attributes["selected"] is not null);
            if (selected is not null)
                fields[name] = selected.GetAttributeValue("value", "");
        }

        return fields;
    }

    private static Dictionary<string, bool> ParseAlpineExists(string html)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(html, @"\b(\w+_exists)\s*:\s*(true|false)\b", RegexOptions.IgnoreCase))
            result[m.Groups[1].Value] = m.Groups[2].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private static string? ToAbsolute(string? url, string baseUrl)
    {
        if (url is null) return null;
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : baseUrl + (url.StartsWith('/') ? url : "/" + url);
    }

    private static string GenerateTotp(string base32Secret)
    {
        var key = Base32Encoding.ToBytes(base32Secret.ToUpperInvariant().Replace(" ", ""));
        return new Totp(key).ComputeTotp();
    }

    private static HtmlDocument ParseHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }
}

internal sealed record CaptchaFields(string Token, string RandomFieldName, string RandomFieldValue);

internal sealed record EditFormData(
    string? Csrf,
    CaptchaFields? Captcha,
    Dictionary<string, string> Fields,
    Dictionary<string, bool> AlpineExists);
