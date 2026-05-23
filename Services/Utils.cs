using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Serialization;
using MediaDebrid_cli.Models;

namespace MediaDebrid_cli.Services;

public static class Utils
{
    public static string BuildEpisodeKey(int season, int episode) => $"{season}:{episode}";

    public static HashSet<int> ParseRange(string? input)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(input)) return result;

        var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (int.TryParse(part, out var single) && single > 0)
            {
                result.Add(single);
            }
            else
            {
                var rangeParts = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (rangeParts.Length == 2 &&
                    int.TryParse(rangeParts[0], out var start) &&
                    int.TryParse(rangeParts[1], out var end) &&
                    start > 0 && end > 0 && start <= end)
                {
                    for (int i = start; i <= end; i++)
                    {
                        result.Add(i);
                    }
                }
                else
                {
                    // Fail closed for malformed mixed input (e.g., "1,a" or "3-1")
                    return [];
                }
            }
        }
        return result;
    }

    public static (bool Success, string Message, string? Key) UpdateConfiguration(string key, string value)
    {
        var properties = typeof(AppSettings).GetProperties();
        foreach (var prop in properties)
        {
            var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            var propName = attr != null ? attr.Name : prop.Name;

            if (propName.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    object convertedValue = Convert.ChangeType(value, prop.PropertyType);
                    prop.SetValue(Settings.Instance, convertedValue);

                    Settings.Save();
                    return (true, $"Successfully updated '{propName}' to '{value}'", propName);
                }
                catch
                {
                    return (false, $"Failed to convert '{value}' to type {prop.PropertyType.Name} for key '{propName}'", propName);
                }
            }
        }

        return (false, $"Configuration key '{key}' not found.", null);
    }

    public static List<(string Key, string Type, string Description)> GetConfigurationMetadata()
    {
        var metadata = new List<(string Key, string Type, string Description)>();
        var properties = typeof(AppSettings).GetProperties();
        foreach (var prop in properties)
        {
            var jsonAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            var descAttr = prop.GetCustomAttribute<DescriptionAttribute>();

            var propName = jsonAttr != null ? jsonAttr.Name : prop.Name;
            var description = descAttr != null ? descAttr.Description : "";

            metadata.Add((propName, prop.PropertyType.Name, description));
        }
        return metadata;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        if (bytes == 0) return "0 B";
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1000 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return $"{size:F2} {units[unitIndex]}";
    }

    public static bool IsEpisodeExisting(string filename, HashSet<string>? existingEpisodeKeys, HashSet<int> selectedSeasons)
    {
        if (existingEpisodeKeys == null || !Settings.Instance.SkipExistingEpisodes) return false;

        var resolver = new MetadataResolver();
        var meta = resolver.ParseName(filename);
        var episodes = ParseRange(meta.Episode);
        if (episodes.Count > 0)
        {
            var seasons = ParseRange(meta.Season);
            var sNum = seasons.Count > 0 ? (int?)seasons.First() : null;
            if (!sNum.HasValue && selectedSeasons.Count == 1)
            {
                sNum = selectedSeasons.First();
            }

            if (sNum.HasValue && episodes.Any(e => existingEpisodeKeys.Contains(BuildEpisodeKey(sNum.Value, e))))
            {
                return true;
            }
        }
        return false;
    }
}
