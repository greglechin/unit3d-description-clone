namespace Unit3dDescriptionClone.Config;

internal static class IniConfig
{
    public static Dictionary<string, List<Dictionary<string, string>>> Load(string path)
    {
        var result = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? currentSection = null;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var sectionName = line[1..^1].Trim();
                currentSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!result.TryGetValue(sectionName, out var list))
                    result[sectionName] = list = [];
                list.Add(currentSection);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || currentSection is null)
                continue;

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            currentSection[key] = currentSection.TryGetValue(key, out var existing)
                ? existing + "\n" + val
                : val;
        }

        return result;
    }
}
