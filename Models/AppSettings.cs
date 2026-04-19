using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MediaDebrid_cli.Models;

public class AppSettings
{
    [JsonPropertyName("real_debrid_api_key")]
    [Description("Required. Your Real-Debrid API token")]
    public string RealDebridApiToken { get; set; } = "";

    [JsonPropertyName("media_root")]
    [Description("Base download path (default: ./media)")]
    public string MediaRoot { get; set; } = "./media";

    [JsonPropertyName("parallel_download")]
    [Description("Enable chunked downloads (default: true)")]
    public bool ParallelDownloadEnabled { get; set; } = true;

    [JsonPropertyName("connections_per_file")]
    [Description("Parallel connections per file (default: 8)")]
    public int ConnectionsPerFile { get; set; } = 8;

    [JsonPropertyName("tmdb_access_token")]
    [Description("Required. TMDB read token")]
    public string TmdbReadAccessToken { get; set; } = "";

    [JsonPropertyName("tmdb_cache_ttl")]
    [Description("TMDB cache duration in seconds (default: 86400)")]
    public int TmdbCacheTtlSeconds { get; set; } = 86400;

    [JsonPropertyName("skip_existing_episodes")]
    [Description("Skip already downloaded episodes (default: true)")]
    public bool SkipExistingEpisodes { get; set; } = true;
}
