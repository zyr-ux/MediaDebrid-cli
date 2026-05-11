using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MediaDebrid_cli.Models;
using MediaDebrid_cli.SecretsManager;

namespace MediaDebrid_cli.Services;

public static class Utils
{
    public static void ApplyMetadataOverrides(MediaMetadata meta, string? seasonOverride, string? episodeOverride)
    {
        if (!string.IsNullOrEmpty(seasonOverride)) meta.Season = seasonOverride;
        if (!string.IsNullOrEmpty(episodeOverride)) meta.Episode = episodeOverride;
        if (meta.Season == null && meta.Type == "show") meta.Season = "1";
    }

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

    public static string BuildEpisodeKey(int season, int episode) => $"{season}:{episode}";

    public static List<int> GetAvailableSeasons(IEnumerable<TorrentFile> files, MetadataResolver resolver)
    {
        return files
            .Select(f =>
            {
                var meta = resolver.ParseName(f.Path);
                var seasons = ParseRange(meta.Season);
                return seasons.Count > 0 ? (int?)seasons.First() : null;
            })
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .Distinct()
            .OrderBy(s => s)
            .ToList();
    }

    public static List<int> GetAvailableEpisodes(IEnumerable<TorrentFile> files, HashSet<int> selectedSeasons, MetadataResolver resolver)
    {
        return files
            .Where(f =>
            {
                if (f.Bytes < 50_000_000) return false;
                var meta = resolver.ParseName(f.Path);
                var fileSeasons = ParseRange(meta.Season);
                return selectedSeasons.Count == 0 || fileSeasons.Any(s => selectedSeasons.Contains(s));
            })
            .SelectMany(f =>
            {
                var meta = resolver.ParseName(f.Path);
                return ParseRange(meta.Episode);
            })
            .Distinct()
            .OrderBy(e => e)
            .ToList();
    }

    public static HashSet<int> DetermineSelectedSeasons(IEnumerable<TorrentFile> files, string? seasonOverride, string? resolvedSeason, MetadataResolver resolver)
    {
        var selectedSeasons = ParseRange(seasonOverride);
        if (selectedSeasons.Count == 0)
        {
            selectedSeasons = files
                .Select(f =>
                {
                    var meta = resolver.ParseName(f.Path);
                    var seasons = ParseRange(meta.Season);
                    return seasons.Any() ? (int?)seasons.First() : null;
                })
                .Where(s => s.HasValue)
                .Select(s => s!.Value)
                .ToHashSet();

            if (selectedSeasons.Count == 0 && !string.IsNullOrEmpty(resolvedSeason))
            {
                selectedSeasons = ParseRange(resolvedSeason);
            }

            if (selectedSeasons.Count == 0) selectedSeasons.Add(1);
        }
        return selectedSeasons;
    }

    public static string[] GetSelectedFiles(List<TorrentFile> files, string? seasonOverride, string? episodeOverride, HashSet<string>? existingEpisodeKeys = null)
    {
        var sRange = ParseRange(seasonOverride);
        var eRange = ParseRange(episodeOverride);
        var resolver = new MetadataResolver();

        var fileIds = files
            .Where(f =>
            {
                if (f.Bytes < 50_000_000 && !eRange.Any()) return false;
                
                var meta = resolver.ParseName(f.Path);
                var fileSeasons = ParseRange(meta.Season);
                var fileEpisodes = ParseRange(meta.Episode);

                if (sRange.Any())
                {
                    if (!fileSeasons.Any(s => sRange.Contains(s))) return false;
                }

                if (eRange.Any())
                {
                    if (!fileEpisodes.Any(e => eRange.Contains(e))) return false;
                }
                
                if (existingEpisodeKeys != null && Settings.Instance.SkipExistingEpisodes)
                {
                    if (fileEpisodes.Any())
                    {
                        foreach (var ep in fileEpisodes)
                        {
                            var season = fileSeasons.FirstOrDefault();
                            if (season == 0 && sRange.Count == 1)
                            {
                                season = sRange.First();
                            }

                            if (season > 0 && existingEpisodeKeys.Contains(BuildEpisodeKey(season, ep))) return false;
                        }
                    }
                }

                return true;
            })
            .Select(f => f.Id.ToString())
            .ToArray();

        return fileIds;
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



    private static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".wmv" };

    public static HashSet<int> GetExistingEpisodes(string directory)
    {
        var existing = new HashSet<int>();
        if (!Directory.Exists(directory)) return existing;

        var resolver = new MetadataResolver();
        try
        {
            var files = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                if (VideoExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                {
                    var meta = resolver.ParseName(Path.GetFileName(file));
                    var eps = ParseRange(meta.Episode);
                    foreach (var ep in eps) existing.Add(ep);
                }
            }
        }
        catch { /* Ignore IO errors during scan */ }

        return existing;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        if (bytes == 0) return "0 B";
        int unitIndex = (int)Math.Floor(Math.Log(bytes, 1024));
        if (unitIndex >= units.Length) unitIndex = units.Length - 1;
        double size = bytes / Math.Pow(1024, unitIndex);
        return $"{size:F2} {units[unitIndex]}";
    }

    public static (HashSet<string>? ExistingEpisodeKeys, int SkippedCount, bool AllSkipped, string ErrorMessage, string? WarnMessage) ValidateExistingFiles(
        MediaMetadata resolved, 
        TorrentInfo info, 
        HashSet<int> selectedSeasons, 
        string? seasonOverride, 
        string? episodeOverride)
    {
        if (!Settings.Instance.SkipExistingEpisodes) return (null, 0, false, string.Empty, null);

        if (resolved.Type == "show")
        {
            var seasonsToCheck = selectedSeasons;
            var existingEpisodeKeys = new HashSet<string>();
            foreach (var s in seasonsToCheck)
            {
                var seasonDir = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, s);
                var existingInSeason = GetExistingEpisodes(seasonDir);
                foreach (var ep in existingInSeason)
                {
                    existingEpisodeKeys.Add(BuildEpisodeKey(s, ep));
                }
            }

            if (existingEpisodeKeys.Count > 0)
            {
                var epRange = ParseRange(episodeOverride);
                var sRange = ParseRange(seasonOverride);
                var resolver = new MetadataResolver();

                var episodesInTorrent = info.Files
                    .Where(f => {
                        if (f.Bytes < 50_000_000 && epRange.Count == 0) return false;
                        var meta = resolver.ParseName(f.Path);
                        var fileSeasons = ParseRange(meta.Season);
                        return sRange.Count == 0 || fileSeasons.Any(s => sRange.Contains(s));
                    })
                    .SelectMany(f => {
                        var meta = resolver.ParseName(f.Path);
                        var fileEpisodes = ParseRange(meta.Episode);
                        var fileSeasons = ParseRange(meta.Season);
                        var s = fileSeasons.FirstOrDefault();
                        if (s == 0 && sRange.Count == 1) s = sRange.First();
                        if (s == 0 && seasonsToCheck.Count == 1) s = seasonsToCheck.First();
                        return fileEpisodes.Select(e => BuildEpisodeKey(s > 0 ? s : 1, e));
                    })
                    .ToHashSet();

                if (episodesInTorrent.Count > 0 && episodesInTorrent.All(key => existingEpisodeKeys.Contains(key)))
                {
                    var scope = (selectedSeasons.Count > 1 && string.IsNullOrEmpty(episodeOverride))
                        ? "All seasons and episodes of this show"
                        : (string.IsNullOrEmpty(episodeOverride) ? "All episodes of this show" : $"All selected episodes of this show ({episodeOverride})");
                    return (existingEpisodeKeys, 0, true, $"[bold red]{scope} already exist in your local library.[/]", null);
                }

                var skippedCount = episodesInTorrent.Count(key => existingEpisodeKeys.Contains(key));
                string? warnMessage = null;
                if (skippedCount > 0)
                {
                    warnMessage = $"[yellow]X[/] Found [cyan]{skippedCount}[/] existing episode{(skippedCount == 1 ? "" : "s")} in local library. {(skippedCount == 1 ? "It" : "They")} will be skipped.";
                }

                return (existingEpisodeKeys, skippedCount, false, string.Empty, warnMessage);
            }
            return (existingEpisodeKeys, 0, false, string.Empty, null);
        }
        else
        {
            var largestFile = info.Files.OrderByDescending(f => f.Bytes).FirstOrDefault();
            if (largestFile != null)
            {
                var destPath = PathGenerator.GetDestinationPath(resolved.Type, resolved.Title, resolved.Year, largestFile.Path, seasonOverride);
                if (File.Exists(destPath))
                {
                    var typeLabel = resolved.Type switch {
                        "movie" => "Movie",
                        "game" => "Game",
                        _ => "File"
                    };
                    return (null, 0, true, $"[bold red]{typeLabel} \"{resolved.Title}\" already exists in your local library.[/]", null);
                }
            }
            return (null, 0, false, string.Empty, null);
        }
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
