namespace MediaDebrid_cli.Models;

public class DownloadProgressModel : EventArgs
{
    public string ProgressKey { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedBytesPerSecond { get; set; }
}

