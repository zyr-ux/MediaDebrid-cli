using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MediaDebrid_cli.Models;

public class AppSettings
{
    private string _debridService = "real_debrid";

    [JsonPropertyName("debrid_service")]
    [Description("Active debrid service (real_debrid or torbox) (default: real_debrid)")]
    public string DebridService
    {
        get => _debridService;
        set
        {
            var clean = value?.Trim().ToLower();
            if (clean == "real_debrid" || clean == "torbox")
            {
                _debridService = clean;
            }
            else
            {
                throw new ArgumentException("debrid_service must be either 'real_debrid' or 'torbox'.");
            }
        }
    }

    [JsonPropertyName("real_debrid_api_key")]
    [Description("Required. Your Real-Debrid API token")]
    [JsonIgnore]
    public string RealDebridApiToken { get; set; } = "";

    [JsonPropertyName("torbox_api_key")]
    [Description("Required. Your TorBox API token")]
    [JsonIgnore]
    public string TorBoxApiToken { get; set; } = "";

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

    [JsonPropertyName("skip_existing_episodes")]
    [Description("Skip already downloaded episodes (default: true)")]
    public bool SkipExistingEpisodes { get; set; } = true;
}
