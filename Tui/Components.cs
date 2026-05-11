using System.Collections.Concurrent;
using MediaDebrid_cli.Models;
using MediaDebrid_cli.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MediaDebrid_cli.Tui;

public static class Components
{
    public static readonly Spinner AppSpinner = Spinner.Known.Arc;

    public static void ShowLogo()
    {
        AnsiConsole.Write(new FigletText("MediaDebrid").Color(Color.Green));
    }

    public static void RenderMetadataPanel(MediaMetadata meta)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap())
            .AddColumn(new GridColumn().PadLeft(1).PadRight(2))
            .AddColumn(new GridColumn());

        void AddGridRow(string label, string value) => grid.AddRow($"[bold]{label}[/]", ":", value);

        AddGridRow("Title", $"[cyan]{Markup.Escape(meta.Title)}[/]");

        if (meta.Type != "other" && !string.IsNullOrWhiteSpace(meta.Year))
        {
            AddGridRow("Year", $"[cyan]{Markup.Escape(meta.Year)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Type))
        {
            AddGridRow("Type", $"[cyan]{char.ToUpper(meta.Type[0]) + meta.Type[1..]}[/]");
        }

        if (!string.IsNullOrEmpty(meta.Season))
        {
            AddGridRow("Season", $"[orange1]{Markup.Escape(meta.Season)}[/]");
        }

        if (!string.IsNullOrEmpty(meta.Episode))
        {
            AddGridRow("Episode", $"[orange1]{Markup.Escape(meta.Episode)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Version))
        {
            AddGridRow("Version", $"[cyan]{Markup.Escape(meta.Version)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Edition))
        {
            AddGridRow("Edition", $"[yellow]{Markup.Escape(meta.Edition)}[/]");
        }

        if (meta.HasDlc == true)
        {
            AddGridRow("Has DLC", "[green]Yes[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.ReleaseGroup))
        {
            AddGridRow("Group", $"[cyan]{Markup.Escape(meta.ReleaseGroup)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Resolution))
        {
            AddGridRow("Resolution", $"[cyan]{Markup.Escape(meta.Resolution)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Quality))
        {
            AddGridRow("Quality", $"[cyan]{Markup.Escape(meta.Quality)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Codec))
        {
            AddGridRow("Codec", $"[cyan]{Markup.Escape(meta.Codec)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.InstallerType))
        {
            AddGridRow("Installer", $"[cyan]{Markup.Escape(meta.InstallerType)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Source))
        {
            AddGridRow("Source", $"[dim]{Markup.Escape(meta.Source)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Destination))
        {
            AddGridRow("Location", $"[dim]{Markup.Escape(meta.Destination)}[/]");
        }

        var panel = new Panel(grid)
        {
            Header = new PanelHeader("Resolved Metadata", Justify.Center),
            Border = BoxBorder.Rounded,
            Expand = true
        };
        AnsiConsole.Write(panel);
    }

    public static void ListConfiguration()
    {
        AnsiConsole.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.White);

        table.AddColumn("[bold]Key[/]");
        table.AddColumn("[bold]Value[/]");

        var metadata = Utils.GetConfigurationMetadata();
        foreach (var (key, type, description) in metadata)
        {
            string value = key switch
            {
                "real_debrid_api_key" => Settings.Instance.RealDebridApiToken,
                "media_root" => Settings.IsDefault(Settings.Instance.MediaRoot) ? $"default ({Settings.MediaRoot})" : Settings.Instance.MediaRoot,
                "games_root" => Settings.IsDefault(Settings.Instance.GamesRoot) ? $"default ({Settings.GamesRoot})" : Settings.Instance.GamesRoot,
                "others_root" => Settings.IsDefault(Settings.Instance.OthersRoot) ? $"default ({Settings.OthersRoot})" : Settings.Instance.OthersRoot,
                "parallel_download" => Settings.Instance.ParallelDownloadEnabled.ToString().ToLower(),
                "connections_per_file" => Settings.Instance.ConnectionsPerFile.ToString(),
                "skip_existing_episodes" => Settings.Instance.SkipExistingEpisodes.ToString().ToLower(),
                _ => "N/A"
            };

            // Highlight the API key differently
            var displayValue = key == "real_debrid_api_key" && !string.IsNullOrEmpty(value)
                ? $"[green]{value}[/]"
                : $"[white]{value}[/]";

            table.AddRow(
                $"[yellow]{key}[/]",
                displayValue
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey] Use 'mediadebrid set <key> <value>' to modify these settings[/]");
        AnsiConsole.WriteLine();
    }
}

internal sealed class CustomTransferSpeedColumn(ConcurrentDictionary<int, double> speeds) : ProgressColumn
{
    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        speeds.TryGetValue(task.Id, out var speed);
        return new Text($"{Utils.FormatBytes((long)speed)}/s", new Style(Color.Silver));
    }
}

internal sealed class CustomDownloadedColumn : ProgressColumn
{
    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        var downloaded = (long)task.Value;
        var total = (long)task.MaxValue;

        if (total <= 0) return new Text("- / -");

        var downloadedStr = Utils.FormatBytes(downloaded);
        var totalStr = Utils.FormatBytes(total);

        return new Markup($"[blue]{downloadedStr}[/] / [green]{totalStr}[/]");
    }
}

internal sealed class CustomEtaColumn(ConcurrentDictionary<int, double> speeds) : ProgressColumn
{
    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        speeds.TryGetValue(task.Id, out var speed);
        if (speed <= 0) return new Text("--");

        var remainingBytes = task.MaxValue - task.Value;
        var remainingSeconds = remainingBytes / speed;
        var eta = TimeSpan.FromSeconds(remainingSeconds);

        if (eta.TotalHours >= 1)
        {
            return new Text($"{(int)eta.TotalHours}h:{eta.Minutes:D2}m");
        }

        return new Text($"{(int)eta.TotalMinutes}m:{eta.Seconds:D2}s");
    }
}

internal sealed class SpinnerColumn(
    ConcurrentDictionary<int, TuiApp.TaskDisplayStatus> taskDisplayStatuses,
    ConcurrentDictionary<int, int> frozenFrames,
    Downloader downloader) : ProgressColumn
{
    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        if (taskDisplayStatuses.TryGetValue(task.Id, out var status))
        {
            switch (status)
            {
                case TuiApp.TaskDisplayStatus.Finished: return new Markup("[bold green]✓[/] ");
                case TuiApp.TaskDisplayStatus.Saved:
                    frozenFrames.TryGetValue(task.Id, out var sIdx);
                    sIdx %= Components.AppSpinner.Frames.Count;
                    var sFrame = Components.AppSpinner.Frames[sIdx];
                    return new Markup($"[bold blue]{Markup.Escape(sFrame)}[/] ");
                case TuiApp.TaskDisplayStatus.Cancelled: return new Markup("[bold red]X[/] ");
            }
        }

        if (task.IsFinished)
        {
            return new Markup("[bold green]✓[/] ");
        }

        if (downloader.IsPaused)
        {
            frozenFrames.TryGetValue(task.Id, out var pIdx);
            pIdx %= Components.AppSpinner.Frames.Count;
            var pFrame = Components.AppSpinner.Frames[pIdx];
            return new Markup($"[bold yellow]{Markup.Escape(pFrame)}[/] ");
        }

        var frameIndex = (int)((Environment.TickCount64 / (long)Components.AppSpinner.Interval.TotalMilliseconds) % Components.AppSpinner.Frames.Count);
        var activeFrame = Components.AppSpinner.Frames[frameIndex];
        return new Markup($"[bold yellow]{Markup.Escape(activeFrame)}[/] ");
    }
}

internal sealed class EpisodeColumn(ConcurrentDictionary<int, string> episodeTexts) : ProgressColumn
{
    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        if (episodeTexts.TryGetValue(task.Id, out var epText))
        {
            return new Markup($"[orange1]{Markup.Escape(epText)}[/] ");
        }
        return Text.Empty;
    }
}
