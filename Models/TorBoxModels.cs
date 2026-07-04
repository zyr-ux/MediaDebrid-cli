using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaDebrid_cli.Models;

public class TorBoxResponse<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

public class TorBoxCreateTorrentResponse
{
    [JsonPropertyName("torrent_id")]
    public JsonElement TorrentId { get; set; }

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;
}

public class TorBoxTorrentItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("download_state")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<TorBoxFile>? Files { get; set; }
}

public class TorBoxFile
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public class TorBoxCheckCachedItem
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("cached")]
    public bool Cached { get; set; }

    [JsonPropertyName("files")]
    public List<TorBoxCheckCachedFile>? Files { get; set; }
}

public class TorBoxCheckCachedFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
