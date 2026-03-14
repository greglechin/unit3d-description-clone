namespace Unit3dDescriptionClone.Config;

internal static class IniConfig
{
    public static Dictionary<string, Dictionary<string, string>> Load(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = "";

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                result.TryAdd(section, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || section.Length == 0)
                continue;

            result[section][line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        return result;
    }
}
