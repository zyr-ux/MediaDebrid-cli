using System.Text.RegularExpressions;

namespace MediaDebrid_cli.Services;

public static class PathGenerator
{
    public static string GetDestinationPath(string? mediaType, string? title, string? year, string filename, int? season = null)
    {
        string safeFilename = Sanitize(filename);
        string baseDir = GetSeasonDirectory(mediaType, title, year, season);
        return Path.Combine(baseDir, safeFilename);
    }

    public static string GetSeasonDirectory(string? mediaType, string? title, string? year, int? season = null)
    {
        string safeTitle = Sanitize(title ?? "Unknown");
        string safeYear = Sanitize(year ?? "");
        string safeType = mediaType ?? "other";

        string folderName = string.IsNullOrWhiteSpace(safeYear) ? safeTitle : $"{safeTitle} ({safeYear})";

        if (safeType.Equals("movie", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(Settings.MediaRoot, "Movies", folderName);
        }
        else if (safeType.Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            string seasonStr = season.HasValue ? $"Season {season.Value:D2}" : "Season 01";
            return Path.Combine(Settings.MediaRoot, "TV Shows", folderName, seasonStr);
        }
        else if (safeType.Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(Settings.GamesRoot, "Games", folderName);
        }
        else
        {
            return Path.Combine(Settings.OthersRoot, "Other");
        }
    }

    private static string Sanitize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        
        // Remove invalid characters but allow directory separators for subpaths
        string invalidChars = new string(Path.GetInvalidFileNameChars())
            .Replace("/", "").Replace("\\", ""); 
        invalidChars += new string(Path.GetInvalidPathChars());

        var regex = new Regex($"[{Regex.Escape(invalidChars)}]");
        return regex.Replace(input, "").Trim();
    }
}
