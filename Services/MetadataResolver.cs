using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaDebrid_cli.Models;
using TMDbLib.Client;

namespace MediaDebrid_cli.Services;

public class MetadataResolver
{
    private readonly TMDbClient? _tmdb;
    private static readonly HttpClient _httpClient = new();

    public MetadataResolver()
    {
        if (!string.IsNullOrWhiteSpace(Settings.TmdbReadAccessToken) && Settings.TmdbReadAccessToken != "your_tmdb_bearer_token_here")
        {
            _tmdb = new TMDbClient(Settings.TmdbReadAccessToken);
        }
        
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "MediaDebrid-cli");
        }
    }

    public async Task<MediaMetadata> ResolveAsync(string name, string? mediaTypeHint = null, CancellationToken cancellationToken = default)
    {
        var parsed = ParseName(name);
        parsed.Source = name;
        string mediaType = mediaTypeHint ?? parsed.Type ?? "movie";
        if (string.IsNullOrEmpty(mediaTypeHint) && mediaType == "movie")
        {
            // double check for show indicators if not explicitly set
            if (parsed.Season.HasValue || parsed.Episode.HasValue
                || name.Contains("S0", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Season", StringComparison.OrdinalIgnoreCase))
            {
                mediaType = "show";
            }
        }

        // 1. Try RAWG for Games
        if (mediaType == "game" && !string.IsNullOrWhiteSpace(Settings.RawgApiKey))
        {
            try
            {
                var gameInfo = await ResolveGameAsync(parsed.Title, cancellationToken);
                if (gameInfo != null)
                {
                    parsed.Title = gameInfo.Title;
                    // Only overwrite year if it's empty or the API found a better one
                    if (string.IsNullOrEmpty(parsed.Year)) parsed.Year = gameInfo.Year;
                    parsed.Type = "game";
                }
            }
            catch { /* fallback to parsed info */ }
        }

        // 2. Try TMDB for Movies/Shows
        var client = _tmdb;
        if (client == null || mediaType == "other" || mediaType == "game")
        {
            parsed.Type = mediaType;
            return parsed;
        }

        try
        {
            if (mediaType == "movie")
            {
                var results = await client.SearchMovieAsync(parsed.Title, cancellationToken: cancellationToken);
                if (results?.Results != null && results.Results.Count > 0)
                {
                    var best = results.Results[0];
                    parsed.Title = best.Title ?? parsed.Title;
                    parsed.Year = best.ReleaseDate?.Year.ToString() ?? parsed.Year;
                }
            }
            else if (mediaType == "show")
            {
                var results = await client.SearchTvShowAsync(parsed.Title, cancellationToken: cancellationToken);
                if (results?.Results != null && results.Results.Count > 0)
                {
                    var best = results.Results[0];
                    parsed.Title = best.Name ?? parsed.Title;
                    parsed.Year = best.FirstAirDate?.Year.ToString() ?? parsed.Year;
                }
            }
        }
        catch
        {
            // fallback to parsed info
        }

        parsed.Type = mediaType;
        return parsed;
    }

    private async Task<MediaMetadata?> ResolveGameAsync(string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Settings.RawgApiKey)) return null;

        try
        {
            string url = $"https://api.rawg.io/api/games?key={Settings.RawgApiKey}&search={Uri.EscapeDataString(title)}&page_size=1";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var results = doc.RootElement.GetProperty("results");

            if (results.GetArrayLength() > 0)
            {
                var first = results[0];
                var name = first.GetProperty("name").GetString() ?? title;
                var released = first.GetProperty("released").GetString();
                var year = !string.IsNullOrEmpty(released) && released.Length >= 4 ? released[..4] : "";

                return new MediaMetadata { Title = name, Year = year, Type = "game" };
            }
        }
        catch { }

        return null;
    }

    public MediaMetadata ParseName(string name)
    {
        var result = new MediaMetadata();
        string titlePart = name;

        // 1. Detect Game Type
        if (Regex.IsMatch(name, @"(?i)\b(fitgirl|dodi|repack|razor1911|reloaded|skidrow|codex|plaza|cpy|steam|gog|cracked|multi\d+|setup-fitgirl)\b"))
        {
            result.Type = "game";
        }

        // 2. Detect Non-Media (Software, ISO, etc.)
        if (Regex.IsMatch(name, @"(?i)\b(windows|office|adobe|autocad|keygen|activator|patch|installer|x64|x86|portable)\b") ||
            Regex.IsMatch(name, @"(?i)\.(exe|msi|iso|zip|rar|7z|dmg|pkg)$"))
        {
            result.Type = "other";
        }

        // 3. Extract Version (v1.2.3, version 1.0, etc.)
        var versionMatch = Regex.Match(name, @"(?i)\b(v|version\s*)(?<ver>\d+(\.\d+)+)\b");
        if (versionMatch.Success)
        {
            result.Version = "v" + versionMatch.Groups["ver"].Value;
            // The title usually ends before the version starts if it's a game or software
            if ((result.Type == "game" || result.Type == "other") && versionMatch.Index > 2)
            {
                titlePart = name[..versionMatch.Index];
            }
        }

        // 4. Extract Season/Episode
        var seMatch = Regex.Match(name, @"(?i)S(?<season>\d{1,2})[E\.]?(?<episode>\d{1,2})|Season\s*(?<season2>\d{1,2})|(?<season3>\d{1,2})x(?<episode2>\d{1,2})");
        if (seMatch.Success)
        {
            result.Type = "show";
            var sStr = seMatch.Groups["season"].Value ?? seMatch.Groups["season2"].Value ?? seMatch.Groups["season3"].Value;
            var eStr = seMatch.Groups["episode"].Value ?? seMatch.Groups["episode2"].Value;

            if (int.TryParse(sStr, out int s)) result.Season = s;
            if (int.TryParse(eStr, out int e)) result.Episode = e;
            
            titlePart = name[..seMatch.Index];
        }
        else if (result.Type != "other") // Skip Year extraction for "Other" types
        {
            // 5. Extract Year
            var yearMatch = Regex.Match(name, @"\b(?<year>19\d{2}|20\d{2})\b");
            if (yearMatch.Success)
            {
                result.Year = yearMatch.Groups["year"].Value;
                if (string.IsNullOrEmpty(result.Type)) result.Type = "movie";
                if (titlePart.Length > yearMatch.Index) titlePart = name[..yearMatch.Index];
            }
        }

        // 6. Extract Resolution
        var resMatch = Regex.Match(name, @"(?i)\b(2160p|1080p|720p|480p|4k|uhd|hd)\b");
        if (resMatch.Success)
        {
            result.Resolution = resMatch.Groups[1].Value.ToLowerInvariant();
            if (titlePart.Length > resMatch.Index) titlePart = titlePart[..resMatch.Index];
        }

        // 7. Extract Quality/Codec
        var qualityMatch = Regex.Match(name, @"(?i)\b(bluray|brrip|bdrip|web-dl|webrip|hdtv|h264|x264|h265|x265|hevc|avc)\b");
        if (qualityMatch.Success)
        {
            result.Quality = qualityMatch.Groups[1].Value.ToLowerInvariant();
        }

        // 8. Clean Title - Handle Brackets and Parens
        // Games often have [FitGirl] or (v1.0) at the end. 
        // If we still have brackets/parens in titlePart, truncate at the first one.
        int firstBracket = titlePart.IndexOfAny(new[] { '[', '(' });
        if (firstBracket > 2)
        {
            titlePart = titlePart[..firstBracket];
        }

        string cleanedTitle = titlePart.Replace(".", " ").Replace("_", " ").Replace(":", " ").Trim();
        cleanedTitle = Regex.Replace(cleanedTitle, @"\s+", " ").Trim('-', ' ');

        // Fallback if title is empty or too short
        if (string.IsNullOrWhiteSpace(cleanedTitle) || cleanedTitle.Length < 2)
        {
            cleanedTitle = name.Split(new[] { '.', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        result.Title = cleanedTitle;
        if (string.IsNullOrEmpty(result.Type)) result.Type = "movie";

        return result;
    }
}
