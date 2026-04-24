using System.Text.Json;
using System.Text.Json.Serialization;
using MediaDebrid_cli.Models;

namespace MediaDebrid_cli.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ResumeMetadata))]
[JsonSerializable(typeof(RealDebridErrorResponse))]
[JsonSerializable(typeof(TorrentAddResponse))]
[JsonSerializable(typeof(List<TorrentItem>))]
[JsonSerializable(typeof(TorrentInfo))]
[JsonSerializable(typeof(UnrestrictResponse))]
internal partial class MediaDebridJsonContext : JsonSerializerContext
{
}

