using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaDebrid_cli.Models;

namespace MediaDebrid_cli.Features.Debrid_Manager;

public interface IDebridClient
{
    Task<TorrentAddResponse> AddMagnetAsync(string magnet, CancellationToken cancellationToken = default);
    Task<TorrentInfo> GetTorrentInfoAsync(string torrentId, CancellationToken cancellationToken = default);
    Task<(string TorrentId, TorrentItem? MatchedItem, bool IsCached, bool NewlyAdded)> AddMagnetAndCheckCacheAsync(string magnet, string hash, CancellationToken cancellationToken = default);
    Task SelectFilesAsync(string torrentId, string fileIds, CancellationToken cancellationToken = default);
    Task<UnrestrictResponse> UnrestrictLinkAsync(string link, CancellationToken cancellationToken = default);
    Task<List<UnrestrictResponse>> UnrestrictLinksAsync(IEnumerable<string> links, CancellationToken cancellationToken = default);
    Task DeleteTorrentAsync(string torrentId, CancellationToken cancellationToken = default);
    Task<TorrentItem?> FindTorrentByHashAsync(string hash, CancellationToken cancellationToken = default);
    Task<TorrentInfo> WaitForStatusAsync(string torrentId, string[] targetStatuses, CancellationToken cancellationToken, int pollDelayMs = 2000);
}
