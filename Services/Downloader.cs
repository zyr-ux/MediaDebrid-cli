using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MediaDebrid_cli.Models;

namespace MediaDebrid_cli.Services;

public class Downloader
{
    private readonly HttpClient _httpClient;
    private static readonly ConcurrentDictionary<string, string> _activeTempFiles = new();
    private bool _isPaused;
    public bool IsPaused => _isPaused;
    public event Action<bool>? OnPauseChanged;

    [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        int dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        ref int lpBytesReturned,
        IntPtr lpOverlapped);

    private const int FSCTL_SET_SPARSE = 0x000900C4;

    private const int FooterSize = 4096;
    private const string MagicMarker = "MDEBRID!";

    static Downloader()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupStatic();
    }

    public static void CleanupStatic()
    {
        CleanupFiles(_activeTempFiles.Keys);
    }

    public static void CleanupFiles(IEnumerable<string> tempPaths, string? rootPath = null, bool force = true)
    {
        foreach (var path in tempPaths.ToList())
        {
            try
            {
                if (force)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                
                string? currentRoot = rootPath;
                if (currentRoot == null)
                {
                    _activeTempFiles.TryGetValue(path, out currentRoot);
                    currentRoot ??= Settings.MediaRoot;
                }

                string? dir = Path.GetDirectoryName(path);
                if (dir != null) DeleteEmptyDirectories(dir, currentRoot);
            }
            catch { /* Ignore cleanup errors */ }
            finally
            {
                _activeTempFiles.TryRemove(path, out _);
            }
        }
    }

    private static void DeleteEmptyDirectories(string? directoryPath, string rootPath)
    {
        if (string.IsNullOrEmpty(directoryPath) || string.IsNullOrEmpty(rootPath)) return;

        try
        {
            var currentDir = new DirectoryInfo(directoryPath);
            var rootDir = new DirectoryInfo(Path.GetFullPath(rootPath));

            while (currentDir != null && currentDir.Exists &&
                   currentDir.FullName.Length > rootDir.FullName.Length &&
                   currentDir.FullName.StartsWith(rootDir.FullName, StringComparison.OrdinalIgnoreCase))
            {
                // Refresh to get latest state
                currentDir.Refresh();
                if (!currentDir.Exists) break;

                // Check if directory is empty
                if (currentDir.GetFileSystemInfos().Length == 0)
                {
                    try
                    {
                        currentDir.Delete();
                    }
                    catch (IOException) { /* Directory might have become non-empty or been deleted */ break; }
                    
                    currentDir = currentDir.Parent;
                }
                else
                {
                    break;
                }
            }
        }
        catch
        {
            // Ignore errors during directory cleanup
        }
    }


    public event EventHandler<DownloadProgressModel>? ProgressChanged;

    private static readonly long UpdateIntervalTicks = TimeSpan.FromMilliseconds(100).Ticks;

    public Downloader()
    {
        _httpClient = new HttpClient();
    }

    public void TogglePause()
    {
        _isPaused = !_isPaused;
        OnPauseChanged?.Invoke(_isPaused);
    }

    public async Task DownloadFileAsync(string url, string destPath, string? rootPath = null, string? progressKey = null, CancellationToken cancellationToken = default, ResumeMetadata? resumeData = null)
    {
        bool segmented = Settings.ParallelDownloadEnabled;
        int segments = Settings.ConnectionsPerFile;
        string resolvedProgressKey = string.IsNullOrWhiteSpace(progressKey) ? destPath : progressKey;

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        if (segmented)
        {
            await DownloadSegmentedAsync(url, destPath, segments, resolvedProgressKey, rootPath, cancellationToken, resumeData);
        }
        else
        {
            await DownloadSingleAsync(url, destPath, resolvedProgressKey, rootPath, 0, cancellationToken, resumeData);
        }
    }

    private async Task DownloadSegmentedAsync(string url, string destPath, int segments, string progressKey, string? rootPath = null, CancellationToken cancellationToken = default, ResumeMetadata? resumeData = null)
    {
        var headReq = new HttpRequestMessage(HttpMethod.Head, url);
        var headRes = await _httpClient.SendAsync(headReq, cancellationToken);
        headRes.EnsureSuccessStatusCode();

        long totalSize = headRes.Content.Headers.ContentLength ?? 0;
        bool acceptRanges = headRes.Headers.AcceptRanges.Contains("bytes");

        if (totalSize == 0 || !acceptRanges || segments <= 1)
        {
            await DownloadSingleAsync(url, destPath, progressKey, rootPath, totalSize, cancellationToken);
            return;
        }

        string tempPath = CreateTempFile(destPath, totalSize, rootPath);
        
        if (resumeData != null)
        {
            resumeData.TotalSize = totalSize;
            if (resumeData.Segments == null || !resumeData.Segments.Any())
            {
                var plannedRanges = PlanByteRanges(totalSize, segments);
                resumeData.Segments = plannedRanges.Select(r => new SegmentProgress { Start = r.Item1, End = r.Item2, Current = r.Item1 }).ToList();
                SaveResumeMetadata(tempPath, resumeData);
            }
        }

        var ranges = (resumeData?.Segments.Select(s => Tuple.Create(s.Current, s.End)).ToList() 
                     ?? PlanByteRanges(totalSize, segments))
                     .Where(r => r.Item1 <= r.Item2).ToList();

        long bytesDownloaded = resumeData?.Segments.Sum(s => s.Current - s.Start) ?? 0;
        long initialBytes = bytesDownloaded;
        long lastUpdateTicks = 0;
        long totalBytesSinceLastSave = 0;
        DateTime startTime = DateTime.UtcNow;

        using var internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = ranges.Select(async range =>
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(range.Item1, range.Item2);

                var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, internalCts.Token);
                res.EnsureSuccessStatusCode();

                using var stream = await res.Content.ReadAsStreamAsync(internalCts.Token);
                using var fileStream = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.Write, 8192, useAsync: true);
                fileStream.Position = range.Item1;

                byte[] buffer = new byte[65536];
                int read;

                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, internalCts.Token)) > 0)
                {
                    while (_isPaused)
                    {
                        await Task.Delay(200, internalCts.Token);
                    }

                    await fileStream.WriteAsync(buffer, 0, read, internalCts.Token);
                    var currentDownloaded = Interlocked.Add(ref bytesDownloaded, read);
                    
                    if (resumeData != null)
                    {
                        var segment = resumeData.Segments.First(s => s.End == range.Item2);
                        Interlocked.Add(ref segment.Current, read);
                        var sinceLast = Interlocked.Add(ref totalBytesSinceLastSave, read);
                        
                        // Save metadata every 5MB total
                        if (sinceLast > 5 * 1024 * 1024)
                        {
                            Interlocked.Exchange(ref totalBytesSinceLastSave, 0);
                            SaveResumeMetadata(tempPath, resumeData);
                        }
                    }

                    long currentTicks = DateTime.UtcNow.Ticks;
                    if (currentTicks - Interlocked.Read(ref lastUpdateTicks) > UpdateIntervalTicks || currentDownloaded == totalSize)
                    {
                        Interlocked.Exchange(ref lastUpdateTicks, currentTicks);
                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        double speed = elapsed > 0 ? (currentDownloaded - initialBytes) / elapsed : 0;

                        ProgressChanged?.Invoke(this, new DownloadProgressModel
                        {
                            ProgressKey = progressKey,
                            Filename = Path.GetFileName(destPath),
                            BytesDownloaded = currentDownloaded,
                            TotalBytes = totalSize,
                            SpeedBytesPerSecond = speed
                        });
                    }
                }
            }
            catch
            {
                internalCts.Cancel();
                throw;
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);
            FinalizeDownload(tempPath, destPath);
        }
        catch
        {
            internalCts.Cancel();
            try { await Task.WhenAll(tasks); } catch { } 
            
            // Final save on error/cancellation
            if (resumeData != null) SaveResumeMetadata(tempPath, resumeData);
            throw;
        }
    }

    private async Task DownloadSingleAsync(string url, string destPath, string progressKey, string? rootPath = null, long totalSize = 0, CancellationToken cancellationToken = default, ResumeMetadata? resumeData = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeData != null && resumeData.Segments.Any())
        {
            var current = resumeData.Segments[0].Current;
            if (current > 0)
            {
                req.Headers.Range = new RangeHeaderValue(current, null);
            }
        }

        var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        res.EnsureSuccessStatusCode();

        totalSize = totalSize > 0 ? totalSize : (res.Content.Headers.ContentLength ?? 0);
        if (resumeData != null && resumeData.Segments.Any()) totalSize = resumeData.TotalSize;

        string tempPath = CreateTempFile(destPath, totalSize, rootPath);
        
        long bytesDownloaded = resumeData?.Segments.FirstOrDefault()?.Current ?? 0;
        long initialBytes = bytesDownloaded;
        long lastUpdateTicks = 0;
        DateTime startTime = DateTime.UtcNow;

        try
        {
            using var stream = await res.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.Write, 8192, useAsync: true);
            fileStream.Position = bytesDownloaded;

            byte[] buffer = new byte[65536];
            int read;
            int bytesSinceLastSave = 0;

            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                while (_isPaused)
                {
                    await Task.Delay(200, cancellationToken);
                }

                await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
                bytesDownloaded += read;

                if (resumeData != null)
                {
                    if (!resumeData.Segments.Any())
                    {
                        resumeData.Segments.Add(new SegmentProgress { Start = 0, End = totalSize - 1, Current = bytesDownloaded });
                    }
                    else
                    {
                        resumeData.Segments[0].Current = bytesDownloaded;
                    }
                    
                    bytesSinceLastSave += read;
                    if (bytesSinceLastSave > 5 * 1024 * 1024)
                    {
                        SaveResumeMetadata(tempPath, resumeData);
                        bytesSinceLastSave = 0;
                    }
                }

                long currentTicks = DateTime.UtcNow.Ticks;
                if (currentTicks - lastUpdateTicks > UpdateIntervalTicks || bytesDownloaded == totalSize)
                {
                    lastUpdateTicks = currentTicks;
                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    double speed = elapsed > 0 ? (bytesDownloaded - initialBytes) / elapsed : 0;

                    ProgressChanged?.Invoke(this, new DownloadProgressModel
                    {
                        ProgressKey = progressKey,
                        Filename = Path.GetFileName(destPath),
                        BytesDownloaded = bytesDownloaded,
                        TotalBytes = totalSize,
                        SpeedBytesPerSecond = speed
                    });
                }
            }

            fileStream.Close();
            FinalizeDownload(tempPath, destPath);
        }
        catch
        {
            if (resumeData != null) SaveResumeMetadata(tempPath, resumeData);
            throw;
        }
    }

    private string CreateTempFile(string destPath, long totalSize, string? rootPath = null)
    {
        string tempPath = destPath + ".mdebrid";
        if (File.Exists(tempPath))
        {
            _activeTempFiles.TryAdd(tempPath, rootPath ?? Settings.MediaRoot);
            return tempPath;
        }

        _activeTempFiles.TryAdd(tempPath, rootPath ?? Settings.MediaRoot);

        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                int bytesReturned = 0;
                DeviceIoControl(fs.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, ref bytesReturned, IntPtr.Zero);
            }

            if (totalSize > 0)
            {
                fs.SetLength(totalSize + FooterSize);
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { File.SetAttributes(tempPath, FileAttributes.Hidden); } catch { }
        }

        return tempPath;
    }

    public void SaveResumeMetadata(string tempPath, ResumeMetadata metadata)
    {
        try
        {
            var json = JsonSerializer.Serialize(metadata, Serialization.MediaDebridJsonContext.Default.ResumeMetadata);
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
            var magicBytes = System.Text.Encoding.UTF8.GetBytes(MagicMarker);

            using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            fs.Seek(-FooterSize, SeekOrigin.End);
            
            // Clear footer area first
            byte[] empty = new byte[FooterSize];
            fs.Write(empty, 0, empty.Length);
            
            fs.Seek(-FooterSize, SeekOrigin.End);
            fs.Write(jsonBytes, 0, jsonBytes.Length);
            
            // Magic at the absolute end
            fs.Seek(-magicBytes.Length, SeekOrigin.End);
            fs.Write(magicBytes, 0, magicBytes.Length);
        }
        catch { /* Ignore save errors during download */ }
    }

    public ResumeMetadata? ReadResumeMetadata(string tempPath)
    {
        try
        {
            if (!File.Exists(tempPath)) return null;
            using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < FooterSize) return null;

            // Check magic
            var magicBytes = System.Text.Encoding.UTF8.GetBytes(MagicMarker);
            byte[] readMagic = new byte[magicBytes.Length];
            fs.Seek(-magicBytes.Length, SeekOrigin.End);
            if (fs.Read(readMagic, 0, readMagic.Length) != readMagic.Length) return null;

            if (System.Text.Encoding.UTF8.GetString(readMagic) != MagicMarker) return null;

            // Read JSON part
            fs.Seek(-FooterSize, SeekOrigin.End);
            byte[] buffer = new byte[FooterSize - magicBytes.Length];
            if (fs.Read(buffer, 0, buffer.Length) != buffer.Length) return null;
            
            var json = System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0');
            return JsonSerializer.Deserialize(json, Serialization.MediaDebridJsonContext.Default.ResumeMetadata);
        }
        catch { return null; }
    }

    private void FinalizeDownload(string tempPath, string destPath)
    {
        _activeTempFiles.TryRemove(tempPath, out _);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { File.SetAttributes(tempPath, FileAttributes.Normal); } catch { }
        }

        // Truncate footer
        var metadata = ReadResumeMetadata(tempPath);
        if (metadata != null)
        {
            using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Write))
            {
                fs.SetLength(metadata.TotalSize);
            }
        }

        File.Move(tempPath, destPath, overwrite: true);
    }

    private List<Tuple<long, long>> PlanByteRanges(long totalSize, int segments)
    {
        var ranges = new List<Tuple<long, long>>();
        long baseSize = totalSize / segments;
        long remainder = totalSize % segments;
        long cursor = 0;

        for (int i = 0; i < segments; i++)
        {
            long size = baseSize + (i < remainder ? 1 : 0);
            long start = cursor;
            long end = cursor + size - 1;
            ranges.Add(Tuple.Create(start, end));
            cursor = end + 1;
        }
        return ranges;
    }
}
