using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaDebrid_cli.Models;

namespace MediaDebrid_cli.Features.Debrid_Manager;

public class TorBoxClient : IDebridClient
{
    private const string BaseUrl = "https://api.torbox.app/v1/api";
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, HashSet<long>> _selectedFiles = new();
    private readonly ConcurrentDictionary<string, List<TorBoxFile>> _torrentFileCache = new();

    public TorBoxClient()
    {
        if (string.IsNullOrWhiteSpace(Settings.TorBoxApiToken))
        {
            throw new ConfigurationException("TorBox API token is missing in .env configuration.");
        }

        _client = new HttpClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Settings.TorBoxApiToken);
    }

    public async Task<TorrentAddResponse> AddMagnetAsync(string magnet, CancellationToken cancellationToken = default)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(magnet), "magnet");
        content.Add(new StringContent("false"), "allow_zip");
        content.Add(new StringContent("false"), "as_queued");

        var res = await _client.PostAsync($"{BaseUrl}/torrents/createtorrent", content, cancellationToken);
        var parsed = await HandleResponseAsync<TorBoxCreateTorrentResponse>(res, cancellationToken);

        if (parsed == null)
        {
            throw new TorBoxApiException("CREATE_TORRENT_ERROR", "Failed to create torrent: response was empty", res.StatusCode);
        }

        var idStr = parsed.TorrentId.ValueKind == JsonValueKind.Number 
            ? parsed.TorrentId.GetInt64().ToString() 
            : parsed.TorrentId.GetString();

        if (string.IsNullOrEmpty(idStr))
        {
            throw new TorBoxApiException("CREATE_TORRENT_ERROR", "Failed to create torrent: no torrent ID returned", res.StatusCode);
        }

        return new TorrentAddResponse
        {
            Id = idStr,
            Uri = magnet
        };
    }

    public async Task<List<TorBoxTorrentItem>> GetTorrentsListAsync(CancellationToken cancellationToken = default)
    {
        var res = await _client.GetAsync($"{BaseUrl}/torrents/mylist?limit=1000&bypass_cache=true", cancellationToken);
        var response = await HandleResponseAsync<List<TorBoxTorrentItem>>(res, cancellationToken);
        return response ?? [];
    }

    private async Task<TorBoxTorrentItem?> FetchTorrentByIdAsync(string torrentId, CancellationToken cancellationToken)
    {
        var res = await _client.GetAsync($"{BaseUrl}/torrents/mylist?id={torrentId}&bypass_cache=true", cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);

        if (!res.IsSuccessStatusCode)
        {
            string errorCode = "UNKNOWN_ERROR";
            string detailMessage = $"HTTP Error {res.StatusCode}";
            try
            {
                var errObj = JsonSerializer.Deserialize(body, Serialization.MediaDebridJsonContext.Default.TorBoxResponseObject);
                if (errObj != null)
                {
                    errorCode = errObj.Error ?? "API_ERROR";
                    detailMessage = errObj.Detail;
                }
            }
            catch { }
            throw new TorBoxApiException(errorCode, detailMessage, res.StatusCode);
        }

        // Try list format first (standard mylist response)
        try
        {
            var listResponse = JsonSerializer.Deserialize(body, Serialization.MediaDebridJsonContext.Default.TorBoxResponseListTorBoxTorrentItem);
            if (listResponse is { Success: true, Data: { Count: > 0 } })
            {
                return listResponse.Data[0];
            }
        }
        catch (JsonException) { }

        // Fallback: single object format (mylist with ?id= may return single object)
        try
        {
            var singleResponse = JsonSerializer.Deserialize(body, Serialization.MediaDebridJsonContext.Default.TorBoxResponseTorBoxTorrentItem);
            if (singleResponse is { Success: true, Data: not null })
            {
                return singleResponse.Data;
            }
        }
        catch (JsonException) { }

        return null;
    }

    public async Task<TorrentItem?> FindTorrentByHashAsync(string hash, CancellationToken cancellationToken = default)
    {
        var torrents = await GetTorrentsListAsync(cancellationToken);
        var match = torrents.FirstOrDefault(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
        if (match == null) return null;

        var mappedStatus = MapStatus(match.Status);
        var isAlreadySelected = _selectedFiles.ContainsKey(match.Id.ToString());
        if (!isAlreadySelected && (mappedStatus == "downloaded" || mappedStatus == "downloading"))
        {
            mappedStatus = "waiting_files_selection";
        }

        return new TorrentItem
        {
            Id = match.Id.ToString(),
            Filename = match.Name,
            Hash = match.Hash,
            Status = mappedStatus
        };
    }

    public async Task<bool> CheckCacheOfHashAsync(string hash, CancellationToken cancellationToken = default)
    {
        try
        {
            var res = await _client.GetAsync($"{BaseUrl}/torrents/checkcached?hash={hash}&format=list", cancellationToken);
            var body = await res.Content.ReadAsStringAsync(cancellationToken);
            if (!res.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("success", out var success) || !success.GetBoolean()) return false;
            if (!root.TryGetProperty("data", out var data)) return false;

            // format=list: data is [{hash, cached}, ...]
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("hash", out var h) && h.GetString()?.Equals(hash, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (item.TryGetProperty("cached", out var cached))
                        {
                            return cached.GetBoolean();
                        }
                    }
                }
            }
            // format=object fallback: data is { "hash": true/false }
            else if (data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty(hash.ToLowerInvariant(), out var val) && val.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
                // Also try original case
                if (data.TryGetProperty(hash, out var val2) && val2.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore checkcached lookup errors and assume uncached
        }
        return false;
    }

    public async Task<(string TorrentId, TorrentItem? MatchedItem, bool IsCached, bool NewlyAdded)> AddMagnetAndCheckCacheAsync(string magnet, string hash, CancellationToken cancellationToken = default)
    {
        var matched = await FindTorrentByHashAsync(hash, cancellationToken);
        if (matched != null)
        {
            var isCached = matched.Status == "downloaded" || matched.Status == "waiting_files_selection";
            return (matched.Id, matched, isCached, false);
        }

        var isCachedOnServer = await CheckCacheOfHashAsync(hash, cancellationToken);
        var addRes = await AddMagnetAsync(magnet, cancellationToken);
        var torrentId = addRes.Id;

        matched = new TorrentItem { Id = torrentId, Status = "waiting_files_selection", Hash = hash };

        return (torrentId, matched, isCachedOnServer, true);
    }

    public async Task<TorrentInfo> GetTorrentInfoAsync(string torrentId, CancellationToken cancellationToken = default)
    {
        var t = await FetchTorrentByIdAsync(torrentId, cancellationToken)
            ?? throw new KeyNotFoundException($"TorBox torrent with ID '{torrentId}' was not found.");
        var mappedStatus = MapStatus(t.Status);
        var isAlreadySelected = _selectedFiles.TryGetValue(t.Id.ToString(), out var selectedIds);

        if (!isAlreadySelected && (mappedStatus == "downloaded" || mappedStatus == "downloading"))
        {
            mappedStatus = "waiting_files_selection";
        }

        var info = new TorrentInfo
        {
            Id = t.Id.ToString(),
            Filename = t.Name,
            Hash = t.Hash,
            Status = mappedStatus
        };

        if (t.Files != null)
        {
            foreach (var f in t.Files)
            {
                if (isAlreadySelected && selectedIds != null && !selectedIds.Contains(f.Id))
                {
                    continue; // Filter unselected files locally
                }

                info.Files.Add(new TorrentFile
                {
                    Id = f.Id,
                    Path = f.Name,
                    Bytes = f.Size,
                    Selected = 1
                });

                info.Links.Add($"torbox://{t.Id}/{f.Id}");
            }
        }

        // Cache file info for later use by UnrestrictLinkAsync
        if (t.Files != null)
        {
            _torrentFileCache[t.Id.ToString()] = t.Files;
        }

        return info;
    }

    public Task SelectFilesAsync(string torrentId, string fileIds, CancellationToken cancellationToken = default)
    {
        var ids = fileIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                         .Select(long.Parse)
                         .ToHashSet();
        _selectedFiles[torrentId] = ids;
        return Task.CompletedTask;
    }

    public async Task<UnrestrictResponse> UnrestrictLinkAsync(string link, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(link);
        var torrentId = uri.Host;
        var fileId = uri.AbsolutePath.TrimStart('/');

        // Resolve details for filename and size from cache (avoids redundant API call)
        string filename = $"file_{fileId}";
        long filesize = 0;
        if (_torrentFileCache.TryGetValue(torrentId, out var cachedFiles))
        {
            var f = cachedFiles.FirstOrDefault(file => file.Id.ToString() == fileId);
            if (f != null)
            {
                filename = f.Name;
                filesize = f.Size;
            }
        }
        else
        {
            // Fallback: fetch from API if cache miss
            try
            {
                var torrent = await FetchTorrentByIdAsync(torrentId, cancellationToken);
                if (torrent?.Files != null)
                {
                    _torrentFileCache[torrentId] = torrent.Files;
                    var f = torrent.Files.FirstOrDefault(file => file.Id.ToString() == fileId);
                    if (f != null)
                    {
                        filename = f.Name;
                        filesize = f.Size;
                    }
                }
            }
            catch { }
        }

        // Fetch direct link
        var dlRes = await _client.GetAsync($"{BaseUrl}/torrents/requestdl?torrent_id={torrentId}&file_id={fileId}&token={Settings.TorBoxApiToken}&redirect=false", cancellationToken);
        var cdnUrl = await HandleResponseAsync<string>(dlRes, cancellationToken);

        if (string.IsNullOrEmpty(cdnUrl))
        {
            throw new TorBoxApiException("REQUEST_DL_ERROR", "Failed to retrieve CDN download link: response was empty", dlRes.StatusCode);
        }

        return new UnrestrictResponse
        {
            Id = fileId,
            Filename = filename,
            Filesize = filesize,
            Link = cdnUrl,
            Download = cdnUrl
        };
    }

    public async Task<List<UnrestrictResponse>> UnrestrictLinksAsync(IEnumerable<string> links, CancellationToken cancellationToken = default)
    {
        var responses = new List<UnrestrictResponse>();
        foreach (var link in links)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var unrestricted = await UnrestrictLinkAsync(link, cancellationToken);
            responses.Add(unrestricted);
        }
        return responses;
    }

    public async Task DeleteTorrentAsync(string torrentId, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(torrentId, out var idVal))
        {
            return;
        }

        var json = $"{{\"operation\":\"delete\",\"torrent_id\":{idVal}}}";
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var res = await _client.PostAsync($"{BaseUrl}/torrents/controltorrent", content, cancellationToken);
        await HandleResponseAsync<object>(res, cancellationToken);
    }

    public async Task<TorrentInfo> WaitForStatusAsync(string torrentId, string[] targetStatuses, CancellationToken cancellationToken, int pollDelayMs = 2000)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var info = await GetTorrentInfoAsync(torrentId, cancellationToken);
            if (targetStatuses.Contains(info.Status)) return info;
            await Task.Delay(pollDelayMs, cancellationToken);
        }

        throw new OperationCanceledException();
    }

    private async Task<T> HandleResponseAsync<T>(HttpResponseMessage res, CancellationToken cancellationToken)
    {
        var body = await res.Content.ReadAsStringAsync(cancellationToken);

        if (!res.IsSuccessStatusCode)
        {
            string errorCode = "UNKNOWN_ERROR";
            string detailMessage = $"HTTP Error {res.StatusCode}";

            try
            {
                var errObj = JsonSerializer.Deserialize<TorBoxResponse<object>>(body, Serialization.MediaDebridJsonContext.Default.TorBoxResponseObject);
                if (errObj != null)
                {
                    errorCode = errObj.Error ?? "API_ERROR";
                    detailMessage = errObj.Detail;
                }
            }
            catch { }

            throw new TorBoxApiException(errorCode, detailMessage, res.StatusCode);
        }

        try
        {
            var genericObj = DeserializeResponse<T>(body);
            if (genericObj != null && genericObj.Success)
            {
                return genericObj.Data!;
            }
            else if (genericObj != null)
            {
                throw new TorBoxApiException(genericObj.Error ?? "API_ERROR", genericObj.Detail, res.StatusCode);
            }
        }
        catch (TorBoxApiException) { throw; }
        catch (Exception ex)
        {
            throw new TorBoxApiException("DESERIALIZATION_ERROR", $"Failed to parse TorBox API response: {ex.Message}", res.StatusCode);
        }

        return default!;
    }

    private static string MapStatus(string torBoxStatus)
    {
        return torBoxStatus.ToLowerInvariant() switch
        {
            "cached" or "completed" or "uploading" => "downloaded",
            "downloading" or "checkingresumedata" => "downloading",
            "metadl" => "magnet_conversion",
            "paused" => "paused",
            "stalled (no seeds)" or "incomplete" => "dead",
            "queued" => "queued",
            _ => "downloading"
        };
    }

    private static TorBoxResponse<T> DeserializeResponse<T>(string body)
    {
        object? parsed = typeof(T) switch
        {
            var t when t == typeof(TorBoxCreateTorrentResponse) => JsonSerializer.Deserialize(body, Serialization.MediaDebridJsonContext.Default.TorBoxResponseTorBoxCreateTorrentResponse),
            var t when t == typeof(List<TorBoxTorrentItem>) => JsonSerializer.Deserialize(body, Serialization.MediaDebridJsonContext.Default.TorBoxResponseListTorBoxTorrentItem),
            var t when t == typeof(List<TorBoxCheckCachedItem>) => JsonSerializer.Deserialize(body, Serialization.MediaDebridJsonContext.Default.TorBoxResponseListTorBoxCheckCachedItem),
            var t when t == typeof(string) => JsonSerializer.Deserialize(body, Serialization.MediaDebridJsonContext.Default.TorBoxResponseString),
            var t when t == typeof(object) => JsonSerializer.Deserialize(body, Serialization.MediaDebridJsonContext.Default.TorBoxResponseObject),
            _ => null
        };

        if (parsed is TorBoxResponse<T> result)
        {
            return result;
        }

        throw new InvalidOperationException($"Failed to deserialize TorBox API response for type '{typeof(T).Name}'.");
    }
}
