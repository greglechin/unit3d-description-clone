namespace Unit3dDescriptionClone.Models;

internal sealed class CookieData
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Path { get; set; } = "/";
    public string Domain { get; set; } = "";
    public string? Expires { get; set; }
    public bool Secure { get; set; }
    public bool HttpOnly { get; set; }
}
