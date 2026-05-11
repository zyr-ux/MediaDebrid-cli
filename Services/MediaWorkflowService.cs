using MediaDebrid_cli.Models;

namespace MediaDebrid_cli.Services;

public class MediaWorkflowService(RealDebridClient client, MetadataResolver resolver)
{
    private readonly RealDebridClient _client = client;
    private readonly MetadataResolver _resolver = resolver;

    public async Task<(MediaMetadata Resolved, TorrentInfo Info, string TorrentId)> InitializeMediaAsync(
        string magnet,
        string torrentId,
        string? seasonOverride,
        string? episodeOverride,
        CancellationToken cancellationToken)
    {
        MediaMetadata? resolved = null;
        void ApplyOverridesLocal(MediaMetadata meta) => ApplyOverrides(meta, seasonOverride, episodeOverride);

        var magnetName = MagnetParser.ExtractName(magnet);
        if (!string.IsNullOrEmpty(magnetName))
        {
            resolved = await _resolver.ResolveAsync(magnetName, cancellationToken: cancellationToken);
            ApplyOverridesLocal(resolved);
            resolved.Destination = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, resolved.Season);
        }

        if (string.IsNullOrEmpty(torrentId))
        {
            var addRes = await _client.AddMagnetAsync(magnet, cancellationToken: cancellationToken);
            torrentId = addRes.Id;
        }

        TorrentInfo? info = await _client.GetTorrentInfoAsync(torrentId, cancellationToken: cancellationToken);
        if (resolved == null)
        {
            resolved = await _resolver.ResolveAsync(info.Filename, cancellationToken: cancellationToken);
            ApplyOverridesLocal(resolved);
            resolved.Destination = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, resolved.Season);
        }

        info = await _client.WaitForStatusAsync(torrentId, ["waiting_files_selection", "downloaded", "dead"], cancellationToken);

        return (resolved, info, torrentId);
    }

    public async Task<(HashSet<string>? ExistingEpisodeKeys, string? WarnMessage)> SubmitFileSelectionAsync(
        MediaMetadata resolved,
        TorrentInfo info,
        string torrentId,
        HashSet<int> selectedSeasons,
        string? seasonOverride,
        string? episodeOverride,
        CancellationToken cancellationToken)
    {
        var validation = ValidateExistingFiles(resolved, info, selectedSeasons, seasonOverride, episodeOverride);

        if (validation.AllSkipped)
        {
            throw new TerminationException(validation.ErrorMessage);
        }

        if (info.Status == "waiting_files_selection")
        {
            var fileIds = GetSelectedFiles(info.Files, seasonOverride, episodeOverride, validation.ExistingEpisodeKeys);

            if (fileIds.Length == 0)
            {
                throw new TerminationException("[bold red]X[/] No files found to download.");
            }

            await _client.SelectFilesAsync(torrentId, string.Join(",", fileIds), cancellationToken: cancellationToken);
        }

        return (validation.ExistingEpisodeKeys, validation.WarnMessage);
    }

    public async Task<TorrentInfo> WaitForCachingAsync(string torrentId, CancellationToken cancellationToken)
    {
        return await _client.WaitForStatusAsync(torrentId, ["downloaded", "dead"], cancellationToken, 5000);
    }

    private void ApplyOverrides(MediaMetadata meta, string? seasonOverride, string? episodeOverride)
    {
        if (!string.IsNullOrEmpty(seasonOverride)) meta.Season = seasonOverride;
        if (!string.IsNullOrEmpty(episodeOverride)) meta.Episode = episodeOverride;
        if (meta.Season == null && meta.Type == "show") meta.Season = "1";
    }

    public List<int> GetAvailableSeasons(IEnumerable<TorrentFile> files)
    {
        return files
            .Select(f =>
            {
                var meta = _resolver.ParseName(f.Path);
                var seasons = Utils.ParseRange(meta.Season);
                return seasons.Count > 0 ? (int?)seasons.First() : null;
            })
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .Distinct()
            .OrderBy(s => s)
            .ToList();
    }

    public List<int> GetAvailableEpisodes(IEnumerable<TorrentFile> files, HashSet<int> selectedSeasons)
    {
        return files
            .Where(f =>
            {
                if (f.Bytes < 50_000_000) return false;
                var meta = _resolver.ParseName(f.Path);
                var fileSeasons = Utils.ParseRange(meta.Season);
                return selectedSeasons.Count == 0 || fileSeasons.Any(s => selectedSeasons.Contains(s));
            })
            .SelectMany(f =>
            {
                var meta = _resolver.ParseName(f.Path);
                return Utils.ParseRange(meta.Episode);
            })
            .Distinct()
            .OrderBy(e => e)
            .ToList();
    }

    public HashSet<int> DetermineSelectedSeasons(IEnumerable<TorrentFile> files, string? seasonOverride, string? resolvedSeason)
    {
        var selectedSeasons = Utils.ParseRange(seasonOverride);
        if (selectedSeasons.Count == 0)
        {
            selectedSeasons = files
                .Select(f =>
                {
                    var meta = _resolver.ParseName(f.Path);
                    var seasons = Utils.ParseRange(meta.Season);
                    return seasons.Any() ? (int?)seasons.First() : null;
                })
                .Where(s => s.HasValue)
                .Select(s => s!.Value)
                .ToHashSet();

            if (selectedSeasons.Count == 0 && !string.IsNullOrEmpty(resolvedSeason))
            {
                selectedSeasons = Utils.ParseRange(resolvedSeason);
            }

            if (selectedSeasons.Count == 0) selectedSeasons.Add(1);
        }
        return selectedSeasons;
    }

    private string[] GetSelectedFiles(List<TorrentFile> files, string? seasonOverride, string? episodeOverride, HashSet<string>? existingEpisodeKeys = null)
    {
        var sRange = Utils.ParseRange(seasonOverride);
        var eRange = Utils.ParseRange(episodeOverride);

        var fileIds = files
            .Where(f =>
            {
                if (f.Bytes < 50_000_000 && !eRange.Any()) return false;

                var meta = _resolver.ParseName(f.Path);
                var fileSeasons = Utils.ParseRange(meta.Season);
                var fileEpisodes = Utils.ParseRange(meta.Episode);

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

                            if (season > 0 && existingEpisodeKeys.Contains(Utils.BuildEpisodeKey(season, ep))) return false;
                        }
                    }
                }

                return true;
            })
            .Select(f => f.Id.ToString())
            .ToArray();

        return fileIds;
    }

    private static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".wmv" };

    private HashSet<int> GetExistingEpisodes(string directory)
    {
        var existing = new HashSet<int>();
        if (!System.IO.Directory.Exists(directory)) return existing;

        try
        {
            var files = System.IO.Directory.GetFiles(directory, "*.*", System.IO.SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                if (System.Linq.Enumerable.Contains(VideoExtensions, System.IO.Path.GetExtension(file).ToLowerInvariant()))
                {
                    var meta = _resolver.ParseName(System.IO.Path.GetFileName(file));
                    var eps = Utils.ParseRange(meta.Episode);
                    foreach (var ep in eps) existing.Add(ep);
                }
            }
        }
        catch { /* Ignore IO errors during scan */ }

        return existing;
    }

    private (HashSet<string>? ExistingEpisodeKeys, int SkippedCount, bool AllSkipped, string ErrorMessage, string? WarnMessage) ValidateExistingFiles(
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
                    existingEpisodeKeys.Add(Utils.BuildEpisodeKey(s, ep));
                }
            }

            if (existingEpisodeKeys.Count > 0)
            {
                var epRange = Utils.ParseRange(episodeOverride);
                var sRange = Utils.ParseRange(seasonOverride);

                var episodesInTorrent = info.Files
                    .Where(f =>
                    {
                        if (f.Bytes < 50_000_000 && epRange.Count == 0) return false;
                        var meta = _resolver.ParseName(f.Path);
                        var fileSeasons = Utils.ParseRange(meta.Season);
                        return sRange.Count == 0 || fileSeasons.Any(s => sRange.Contains(s));
                    })
                    .SelectMany(f =>
                    {
                        var meta = _resolver.ParseName(f.Path);
                        var fileEpisodes = Utils.ParseRange(meta.Episode);
                        var fileSeasons = Utils.ParseRange(meta.Season);
                        var s = fileSeasons.FirstOrDefault();
                        if (s == 0 && sRange.Count == 1) s = sRange.First();
                        if (s == 0 && seasonsToCheck.Count == 1) s = seasonsToCheck.First();
                        return fileEpisodes.Select(e => Utils.BuildEpisodeKey(s > 0 ? s : 1, e));
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
                if (System.IO.File.Exists(destPath))
                {
                    var typeLabel = resolved.Type switch
                    {
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

}
