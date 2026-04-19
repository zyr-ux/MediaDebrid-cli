using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MediaDebrid_cli.Models;

public class AppSettings
{
    [JsonPropertyName("real_debrid_api_key")]
    [Description("Required. Your Real-Debrid API token")]
    public string RealDebridApiToken { get; set; } = "";

    [JsonPropertyName("media_root")]
    [Description("Download path for Movies & Shows (default: Downloads/MediaDebrid)")]
    public string MediaRoot { get; set; } = "";

    [JsonPropertyName("games_root")]
    [Description("Download path for Games (default: Downloads/MediaDebrid)")]
    public string GamesRoot { get; set; } = "";

    [JsonPropertyName("others_root")]
    [Description("Download path for miscellaneous files (default: Downloads/MediaDebrid)")]
    public string OthersRoot { get; set; } = "";

    [JsonPropertyName("parallel_download")]
    [Description("Enable chunked downloads (default: true)")]
    public bool ParallelDownloadEnabled { get; set; } = true;

    [JsonPropertyName("connections_per_file")]
    [Description("Parallel connections per file (default: 8)")]
    public int ConnectionsPerFile { get; set; } = 8;

    [JsonPropertyName("tmdb_access_token")]
    [Description("Optional. TMDB read access token for movies and shows metadata")]
    public string TmdbReadAccessToken { get; set; } = "";

    [JsonPropertyName("tmdb_cache_ttl")]
    [Description("TMDB cache duration in seconds (default: 86400)")]
    public int TmdbCacheTtlSeconds { get; set; } = 86400;

    [JsonPropertyName("skip_existing_episodes")]
    [Description("Skip already downloaded episodes (default: true)")]
    public bool SkipExistingEpisodes { get; set; } = true;

    [JsonPropertyName("rawg_api_key")]
    [Description("Optional. RAWG.io API key for game metadata")]
    public string RawgApiKey { get; set; } = "";
}
