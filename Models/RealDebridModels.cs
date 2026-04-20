using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaDebrid_cli.Models;

public class TorrentAddResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;
}

public class TorrentItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

public class TorrentInfo : TorrentItem
{
    [JsonPropertyName("files")]
    public List<TorrentFile> Files { get; set; } = new();

    [JsonPropertyName("links")]
    public List<string> Links { get; set; } = new();
}

public class TorrentFile
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("selected")]
    public int Selected { get; set; }
}

public class UnrestrictResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("filesize")]
    public long Filesize { get; set; }

    [JsonPropertyName("link")]
    public string Link { get; set; } = string.Empty;

    [JsonPropertyName("download")]
    public string Download { get; set; } = string.Empty;
}

public class RealDebridErrorResponse
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("error_code")]
    public int ErrorCode { get; set; }
}


