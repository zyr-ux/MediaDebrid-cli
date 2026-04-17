namespace MediaDebrid_cli.Models;

public class TMDBModels
{
    public string Title { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int? Season { get; set; }
    public int? Episode { get; set; }
}

