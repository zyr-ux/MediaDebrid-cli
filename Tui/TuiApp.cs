using System.Collections.Concurrent;
using MediaDebrid_cli.Models;
using Spectre.Console;
using MediaDebrid_cli.Services;

namespace MediaDebrid_cli.Tui;

public class TuiApp
{
    private IDebridClient? _client;
    private readonly Downloader _downloader;
    private readonly MetadataResolver _metadataResolver;
    private MediaWorkflowService? _workflowService;

    private readonly ConcurrentDictionary<string, ProgressTask> _progressTasks;
    private readonly ConcurrentDictionary<int, double> _taskSpeeds;
    private readonly ConcurrentDictionary<int, TaskDisplayStatus> _taskDisplayStatuses;
    private readonly ConcurrentDictionary<int, int> _frozenFrames;
    private readonly ConcurrentDictionary<int, string> _taskEpisodeTexts;
    private readonly ConcurrentDictionary<int, string> _taskOriginalNames;

    internal enum TaskDisplayStatus { Finished, Saved, Cancelled }

    public TuiApp()
    {
        _downloader = new Downloader();
        _downloader.ProgressChanged += OnDownloadProgressChanged;
        _downloader.OnPauseChanged += OnPauseChanged;
        _metadataResolver = new MetadataResolver();

        _progressTasks = new ConcurrentDictionary<string, ProgressTask>();
        _taskSpeeds = new ConcurrentDictionary<int, double>();
        _taskDisplayStatuses = new ConcurrentDictionary<int, TaskDisplayStatus>();
        _frozenFrames = new ConcurrentDictionary<int, int>();
        _taskEpisodeTexts = new ConcurrentDictionary<int, string>();
        _taskOriginalNames = new ConcurrentDictionary<int, string>();
    }

    private IDebridClient GetClient()
    {
        if (_client != null) return _client;
        if (Settings.Instance.DebridService == "torbox")
            _client = new TorBoxClient();
        else
            _client = new RealDebridClient();
        return _client;
    }
    private MediaWorkflowService GetWorkflowService() => _workflowService ??= new MediaWorkflowService(GetClient(), _metadataResolver);

    public async Task RunAsync(string magnet, string? seasonOverride = null, string? episodeOverride = null, bool showLogo = true, bool forceResume = false, bool generateUnresLinks = false, CancellationToken cancellationToken = default)
    {
        if (showLogo)
        {
            Components.ShowLogo();
        }

        _progressTasks.Clear();
        _taskSpeeds.Clear();
        _taskDisplayStatuses.Clear();
        _frozenFrames.Clear();
        _taskEpisodeTexts.Clear();
        _taskOriginalNames.Clear();

        try
        {
            await Components.EnsureConfiguredAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var torrentId = string.Empty;
        TorrentInfo? info = null;
        MediaMetadata? resolved = null;
        HashSet<string>? existingEpisodeKeys = null;

        var hash = MagnetParser.ExtractHash(magnet);
        if (string.IsNullOrEmpty(hash))
        {
            throw new MagnetException("Invalid magnet link: Missing BTIH hash (xt=urn:btih:).");
        }

        TorrentItem? matched = null;
        bool isCached = false;
        bool newlyAdded = false;

        try
        {
            await AnsiConsole.Status()
                .StartAsync($"Checking {(Settings.Instance.DebridService == "torbox" ? "TorBox" : "Real-Debrid")} cache...", async ctx =>
                {
                    ctx.Spinner(Spinner.Known.Arc);
                    ctx.SpinnerStyle(Style.Parse("yellow"));

                    var result = await GetClient().AddMagnetAndCheckCacheAsync(magnet, hash, cancellationToken);
                    torrentId = result.TorrentId;
                    matched = result.MatchedItem;
                    isCached = result.IsCached;
                    newlyAdded = result.NewlyAdded;
                });
        }
        catch (RealDebridApiException) { throw; }
        catch (TorBoxApiException) { throw; }
        catch (HttpRequestException ex)
        {
            throw new TerminationException($"[bold red]X[/] Network error during cache check: [white]{Markup.Escape(ex.Message)}[/]");
        }

        PrintGap.Print();
        if (!newlyAdded)
        {
            // Existing torrent confirmed.
            PrintGap.MarkupLine($"[bold green]✓[/] Found existing torrent.");
        }
        else
        {
            // Newly added — cache status is unknown until after file selection.
            PrintGap.MarkupLine($"[bold green]✓[/] Magnet added to {(Settings.Instance.DebridService == "torbox" ? "TorBox" : "Real-Debrid")}.");
        }

        await AnsiConsole.Status()
            .StartAsync("Initializing...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Arc);
                ctx.SpinnerStyle(Style.Parse("green"));

                try
                {
                    var initResult = await GetWorkflowService().InitializeMediaAsync(magnet, torrentId, seasonOverride, episodeOverride, cancellationToken);
                    resolved = initResult.Resolved;
                    info = initResult.Info;
                    torrentId = initResult.TorrentId;
                }
                catch (TerminationException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (RealDebridApiException) { throw; }
                catch (TorBoxApiException) { throw; }
                catch (HttpRequestException ex)
                {
                    throw new TerminationException($"[red]X[/] Network error during initialization: [white]{Markup.Escape(ex.Message)}[/]");
                }
                catch (Exception ex)
                {
                    PrintGap.MarkupLine($"[red]Error during initialization:[/] [white]{Markup.Escape(ex.Message)}[/]");
                }
            });

        if (info == null || resolved == null) return;

        if (info.Status == "dead")
        {
            throw new TerminationException("[bold red]X[/] Torrent is dead.");
        }

        List<int> seasonsInTorrent = [];
        if (resolved.Type == "show")
        {
            seasonsInTorrent = GetWorkflowService().GetAvailableSeasons(info.Files);

            if (seasonsInTorrent.Count > 1 && string.IsNullOrEmpty(seasonOverride))
            {
                // Display accurate intent for all-seasons mode
                resolved.Season = "Multiple";
                var defaultSeasonDir = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, 1);
                resolved.Destination = Directory.GetParent(defaultSeasonDir)?.FullName ?? defaultSeasonDir;
            }
        }

        PrintGap.Print();
        Components.RenderMetadataPanel(resolved);
        PrintGap.Print();

        // Interactive season selection for multi-season shows
        if (resolved.Type == "show" && string.IsNullOrEmpty(seasonOverride) && seasonsInTorrent.Count > 1)
        {
            try
            {
                string? input = null;
                var seasonRangeSuggestion = seasonsInTorrent.Count > 0 ? $"{seasonsInTorrent.Min()}-{seasonsInTorrent.Max()}" : "1-3";
                while (!cancellationToken.IsCancellationRequested)
                {
                    input = await Components.ReadLineWithEffectAsync($"[yellow]Multiple seasons detected ({string.Join(", ", seasonsInTorrent.Select(s => $"S{s:D2}"))}).[/]\nEnter [green]season number or range[/] (e.g. {seasonRangeSuggestion}) to download (leave empty for all)", cancellationToken);

                    if (cancellationToken.IsCancellationRequested) break;
                    if (string.IsNullOrWhiteSpace(input)) break;

                    var parsed = Utils.ParseRange(input);
                    if (parsed.Count == 0)
                    {
                        PrintGap.MarkupLine($"[red]Please enter a valid season number or range (e.g., {seasonRangeSuggestion}).[/]");
                        continue;
                    }

                    if (!parsed.Any(s => seasonsInTorrent.Contains(s)))
                    {
                        PrintGap.MarkupLine($"[red]None of the specified seasons ({input}) were found in this torrent.[/]");
                        continue;
                    }
                    break;
                }

                if (!string.IsNullOrWhiteSpace(input))
                {
                    seasonOverride = input;
                    resolved.Season = input;
                    resolved.Destination = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year); // Generic dir for ranges
                    PrintGap.Print();
                    PrintGap.MarkupLine($"[bold green]✓[/] Selected seasons [cyan]{input}[/].");
                }
            }
            catch (OperationCanceledException)
            {
                throw new TerminationException("[red]Application terminated. Exiting...[/]");
            }
        }

        // Interactive episode selection for shows
        if (resolved.Type == "show" && string.IsNullOrEmpty(episodeOverride))
        {
            var sRange = Utils.ParseRange(seasonOverride);
            var episodesInTorrent = GetWorkflowService().GetAvailableEpisodes(info.Files, sRange);

            if (episodesInTorrent.Count == 1)
            {
                PrintGap.Print();
                episodeOverride = episodesInTorrent[0].ToString();
                resolved.Episode = episodeOverride;
                PrintGap.MarkupLine($"[bold green]✓[/] Only one episode detected ([cyan]E{episodesInTorrent[0]:D2}[/]). Auto-selecting.");
            }
            else
            {
                PrintGap.Print();
                try
                {
                    string? input = null;
                    var epRangeSuggestion = episodesInTorrent.Count > 0 ? $"{episodesInTorrent.Min()}-{episodesInTorrent.Max()}" : "1-12";
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        input = await Components.ReadLineWithEffectAsync($"Enter [green]episode number or range[/] (e.g. {epRangeSuggestion}) to download (leave empty for all)", cancellationToken);

                        if (cancellationToken.IsCancellationRequested) break;
                        if (string.IsNullOrWhiteSpace(input)) break;

                        var parsed = Utils.ParseRange(input);
                        if (parsed.Count == 0)
                        {
                            PrintGap.MarkupLine($"[red]Please enter a valid episode number or range (e.g., {epRangeSuggestion}).[/]");
                            continue;
                        }

                        if (!info.Files.Any(f =>
                        {
                            var meta = _metadataResolver.ParseName(f.Path);
                            var fileSeasons = Utils.ParseRange(meta.Season);
                            var fileEpisodes = Utils.ParseRange(meta.Episode);

                            if (sRange.Count > 0 && !fileSeasons.Any(s => sRange.Contains(s))) return false;
                            return fileEpisodes.Any(e => parsed.Contains(e));
                        }))
                        {
                            var scope = sRange.Count > 0 ? "in selected seasons" : "in this torrent";
                            PrintGap.MarkupLine($"[red]No episodes from range {input} found {scope}.[/]");
                            continue;
                        }
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(input))
                    {
                        episodeOverride = input;
                        resolved.Episode = input;
                        PrintGap.Print();
                        PrintGap.MarkupLine($"[bold green]✓[/] Selected episodes [cyan]{input}[/].");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw new TerminationException("[red]Application terminated. Exiting...[/]");
                }
            }
        }

        var selectedSeasons = Utils.ParseRange(seasonOverride);
        if (resolved.Type == "show" && selectedSeasons.Count == 0)
        {
            selectedSeasons = GetWorkflowService().DetermineSelectedSeasons(info.Files, seasonOverride, resolved.Season);
        }

        await AnsiConsole.Status()
            .StartAsync("Preparing selection...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Arc);
                ctx.SpinnerStyle(Style.Parse("green"));

                try
                {
                    var result = await GetWorkflowService().SubmitFileSelectionAsync(resolved, info, torrentId, selectedSeasons, seasonOverride, episodeOverride, cancellationToken);
                    existingEpisodeKeys = result.ExistingEpisodeKeys;

                    if (!string.IsNullOrEmpty(result.WarnMessage))
                    {
                        PrintGap.Print();
                        PrintGap.MarkupLine(result.WarnMessage);
                    }
                }
                catch (TerminationException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (RealDebridApiException) { throw; }
                catch (TorBoxApiException) { throw; }
                catch (HttpRequestException ex)
                {
                    throw new TerminationException($"[red]X[/] Network error during initialization: [white]{Markup.Escape(ex.Message)}[/]");
                }
                catch (Exception ex)
                {
                    PrintGap.MarkupLine($"[red]Error during initialization:[/] [white]{Markup.Escape(ex.Message)}[/]");
                }
            });

        if (info == null || resolved == null) return;

        // Definitive cache check: after file selection the status is authoritative.
        // "downloaded" = cached. Anything else (downloading, queued, etc.) = not cached.
        info = await GetClient().GetTorrentInfoAsync(torrentId, cancellationToken);
        bool wasUncached = info.Status != "downloaded";
        if (wasUncached)
        {
            PrintGap.Print();
            PrintGap.MarkupLine($"[bold yellow]![/] Magnet is [bold yellow]not cached[/] on {(Settings.Instance.DebridService == "torbox" ? "TorBox" : "Real-Debrid")} servers.");
            PrintGap.Suppress();

            bool userCancelled = false;
            await AnsiConsole.Status()
                .StartAsync($"File is being cached by {(Settings.Instance.DebridService == "torbox" ? "TorBox" : "Real-Debrid")} [dim][[press N to stop]][/]...", async ctx =>
                {
                    ctx.Spinner(Spinner.Known.Arc);
                    ctx.SpinnerStyle(Style.Parse("yellow"));

                    using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    var pollTask = Task.Run(async () =>
                    {
                        try
                        {
                            info = await GetWorkflowService().WaitForCachingAsync(torrentId, pollCts.Token);
                        }
                        catch (OperationCanceledException) { }
                    }, pollCts.Token);

                    while (!pollTask.IsCompleted)
                    {
                        if (Console.KeyAvailable)
                        {
                            var key = Console.ReadKey(intercept: true);
                            if (key.KeyChar == 'n' || key.KeyChar == 'N')
                            {
                                userCancelled = true;
                                await pollCts.CancelAsync();
                                break;
                            }
                        }
                        await Task.Delay(100, cancellationToken);
                    }

                    await pollTask; // Ensure it finishes cleanly
                });

            if (userCancelled)
            {
                await AnsiConsole.Status().StartAsync("[red]Stopping cache and removing magnet...[/]", async _ =>
                {
                    await GetClient().DeleteTorrentAsync(torrentId, cancellationToken);
                });
                throw new TerminationException($"[red]Caching stopped by user. Magnet removed from {(Settings.Instance.DebridService == "torbox" ? "TorBox" : "Real-Debrid")} account.[/]");
            }

            if (info.Status == "dead")
            {
                throw new TerminationException("[bold red]X[/] Torrent is dead.");
            }
        }

        if (!wasUncached) PrintGap.Print();
        PrintGap.MarkupLine("[bold green]✓[/] Files are ready and [bold cyan]cached[/]!");

        if (generateUnresLinks)
        {
            PrintGap.Print();
            PrintGap.MarkupLine("[bold]Generating Unrestricted Links...[/]");
            PrintGap.Print();

            await AnsiConsole.Status().StartAsync("[yellow]Unrestricting links...[/]", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Arc);
                var unrestrictedLinks = await GetClient().UnrestrictLinksAsync(info.Links, cancellationToken);
                foreach (var unrestricted in unrestrictedLinks)
                {
                    PrintGap.MarkupLine($"[cyan]{Markup.Escape(unrestricted.Filename)}[/]");
                    PrintGap.MarkupLine($"[white]{unrestricted.Download}[/]");
                    PrintGap.Print();
                }
            });
            return;
        }

        PrintGap.Print();
        PrintGap.MarkupLine("[bold]Starting Downloads...[/]");
        PrintGap.MarkupLine("[dim]Controls: [yellow]P[/] Pause | [green]X[/] Save & Exit | [red]Ctrl+C[/] Cancel & Delete[/]");

        var activePaths = new ConcurrentBag<string>();
        Task? downloadLoopTask = null;
        bool shouldDeletePartial = true;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var queuedDownloads = new List<(UnrestrictResponse Unrestricted, ResumeMetadata? ResumeData, string DestPath)>();
        bool silentExit = false;

        await AnsiConsole.Status().StartAsync("[yellow]Preparing downloads...[/]", async ctx =>
        {
            ctx.Spinner(Spinner.Known.Arc);
            var unrestrictedLinks = await GetClient().UnrestrictLinksAsync(info.Links, linkedCts.Token);
            queuedDownloads = _downloader.PrepareDownloadQueue(
                unrestrictedLinks,
                resolved,
                magnet,
                seasonOverride,
                existingEpisodeKeys,
                selectedSeasons,
                linkedCts.Token);
        });

        // Now ask for confirmations for any detected resumes
        for (int i = 0; i < queuedDownloads.Count; i++)
        {
            var (unrestricted, resumeData, destPath) = queuedDownloads[i];
            if (resumeData != null)
            {
                if (!forceResume) PrintGap.Print();
                if (forceResume || await Components.ConfirmAsync($"[yellow]Partial download found for {Markup.Escape(unrestricted.Filename)} ({Utils.FormatBytes(resumeData.Segments.Sum(s => s.Current - s.Start))} / {Utils.FormatBytes(resumeData.TotalSize)}). Resume?[/]", cancellationToken))
                {
                    // Keep it
                }
                else
                {
                    File.Delete(destPath + ".mdebrid");
                    queuedDownloads[i] = (unrestricted, null, destPath);
                }
            }
        }

        if (queuedDownloads.Count == 0 && !generateUnresLinks)
        {
            var typeLabel = resolved.Type switch
            {
                "show" => "episodes",
                "movie" => "files",
                "game" => "files",
                _ => "files"
            };
            throw new TerminationException($"[bold red]All selected {typeLabel} already exist in your local library.[/]");
        }

        try
        {
            var progressColumns = new List<ProgressColumn>
            {
                new SpinnerColumn(_taskDisplayStatuses, _frozenFrames, _downloader)
            };

            if (resolved.Type == "show")
            {
                progressColumns.Add(new EpisodeColumn(_taskEpisodeTexts));
            }

            progressColumns.AddRange([
                new TaskDescriptionColumn(),
                new ProgressBarColumn { Width = 200 },
                new PercentageColumn(),
                new CustomDownloadedColumn(),
                new CustomTransferSpeedColumn(_taskSpeeds),
                new CustomEtaColumn(_taskSpeeds)
            ]);

            PrintGap.Suppress(); // Suppress gap before padded progress bar
            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns([.. progressColumns])
                .StartAsync(async ctx =>
                {

                    downloadLoopTask = _downloader.DownloadQueueAsync(
                        queuedDownloads,
                        magnet,
                        seasonOverride,
                        episodeOverride,
                        resolved,
                        existingEpisodeKeys,
                        selectedSeasons,
                        activePaths,
                        async (unrestricted, destPath, progressKey, resumeData) =>
                        {
                            var filename = unrestricted.Filename;
                            var displayFilename = filename.Length > 40 ? filename[..37] + "..." : filename;

                            var progressTask = ctx.AddTask($"[cyan]{Markup.Escape(displayFilename)}[/]", new ProgressTaskSettings { AutoStart = false });
                            _progressTasks[progressKey] = progressTask;
                            _taskOriginalNames[progressTask.Id] = displayFilename;

                            if (resolved.Type == "show")
                            {
                                var meta = _metadataResolver.ParseName(filename);
                                var episodes = Utils.ParseRange(meta.Episode);
                                if (episodes.Count > 0)
                                {
                                    var seasons = Utils.ParseRange(meta.Season);
                                    var sNum = seasons.Count > 0 ? seasons.First() : 1;
                                    _taskEpisodeTexts[progressTask.Id] = $"S{sNum:D2}E{episodes.First():D2}";
                                }
                            }

                            if (resumeData.TotalSize > 0)
                            {
                                progressTask.MaxValue = resumeData.TotalSize;
                                progressTask.Value = resumeData.Segments.Sum(s => s.Current - s.Start);
                            }

                            progressTask.StartTask();
                            await Task.CompletedTask;
                        },
                        async (progressKey) =>
                        {
                            if (_progressTasks.TryGetValue(progressKey, out var pt))
                            {
                                pt.Value = pt.MaxValue;
                                pt.StopTask();
                            }
                            await Task.CompletedTask;
                        },
                        async (destPath, ex) =>
                        {
                            shouldDeletePartial = false;
                            var key = destPath;
                            if (_progressTasks.TryGetValue(key, out var pt))
                            {
                                _taskDisplayStatuses[pt.Id] = TaskDisplayStatus.Cancelled;
                                pt.StopTask();
                            }

                            foreach (var t in _progressTasks.Values)
                            {
                                if (!t.IsFinished)
                                {
                                    _taskDisplayStatuses.TryAdd(t.Id, TaskDisplayStatus.Cancelled);
                                }
                            }
                            await Task.CompletedTask;
                        },
                        linkedCts.Token
                    );

                    await MonitorKeypressesAsync(downloadLoopTask, linkedCts, () => shouldDeletePartial = false);

                    if (downloadLoopTask != null) await downloadLoopTask;

                });

            PrintGap.Suppress(); // Suppress gap after padded progress bar

            if (!linkedCts.IsCancellationRequested)
            {
                PrintGap.MarkupLine("[bold green]All downloads completed![/]");
                shouldDeletePartial = false;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (MagnetException) { throw; }
        catch (ConfigurationException) { throw; }
        catch (DownloadException) { throw; }
        catch (RealDebridClientException) { throw; }
        catch (RealDebridApiException) { throw; }
        catch (TorBoxApiException) { throw; }
        catch (Exception ex)
        {
            PrintGap.MarkupLine($"[red]Unexpected error ({Markup.Escape(ex.GetType().Name)}):[/] [white]{Markup.Escape(ex.Message)}[/]");
        }
        finally
        {
            if (downloadLoopTask != null && !downloadLoopTask.IsCompleted)
            {
                try { await downloadLoopTask; }
                catch (Exception)
                {
                    // Suppress background shutdown errors to avoid masking the primary flow outcome.
                }
            }

            if (shouldDeletePartial)
            {
                PrintGap.MarkupLine("[red]Download cancelled. Cleaning up partial files...[/]");
                var cleanupRoot = resolved != null ? Settings.GetRootPathForType(resolved.Type) : null;
                Downloader.CleanupFiles(activePaths, cleanupRoot, force: true);
            }
            else if (linkedCts.IsCancellationRequested)
            {
                PrintGap.MarkupLine("[yellow]Stopping... Partial progress preserved for resume.[/]");
                var cleanupRoot = resolved != null ? Settings.GetRootPathForType(resolved.Type) : null;
                Downloader.CleanupFiles(activePaths, cleanupRoot, force: false);
                silentExit = true;
            }
            else
            {
                var cleanupRoot = resolved != null ? Settings.GetRootPathForType(resolved.Type) : null;
                Downloader.CleanupFiles(activePaths, cleanupRoot, force: false);
            }
        }

        if (silentExit) throw new TerminationException("");
    }

    public async Task RunInteractiveAsync(CancellationToken cancellationToken, bool generateUnresLinks = false)
    {
        try
        {
            await Components.EnsureConfiguredAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Components.ShowLogo();

        string? magnet = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            magnet = await Components.ReadLineWithEffectAsync("Enter [green]Magnet Link[/]: ", cancellationToken, ConsoleColor.Green);

            if (cancellationToken.IsCancellationRequested) break;

            var validation = MagnetParser.Validate(magnet);
            if (!validation.IsValid)
            {
                PrintGap.MarkupLine($"[red]{Markup.Escape(validation.ErrorMessage!)}[/]");
                continue;
            }

            break;
        }

        if (magnet is null || cancellationToken.IsCancellationRequested) return;

        await RunAsync(magnet, showLogo: false, cancellationToken: cancellationToken, generateUnresLinks: generateUnresLinks);
    }

    public async Task RunResumeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            PrintGap.MarkupLine($"[red]Error:[/] File [cyan]{path}[/] not found.");
            return;
        }

        var metadata = _downloader.ReadResumeMetadata(path);
        if (metadata == null)
        {
            PrintGap.MarkupLine($"[red]Error:[/] Could not read resume metadata from [cyan]{path}[/].");
            return;
        }

        await RunAsync(metadata.MagnetUri, metadata.SeasonOverride, metadata.EpisodeOverride, showLogo: true, forceResume: true, cancellationToken: cancellationToken);
    }



    private void OnDownloadProgressChanged(object? sender, DownloadProgressModel e)
    {
        if (_progressTasks.TryGetValue(e.ProgressKey, out var task))
        {
            if (e.TotalBytes > 0)
            {
                task.MaxValue = e.TotalBytes;
            }

            task.Value = e.BytesDownloaded;
            _taskSpeeds[task.Id] = e.SpeedBytesPerSecond;

            if (e.BytesDownloaded >= e.TotalBytes && e.TotalBytes > 0)
            {
                _taskDisplayStatuses[task.Id] = TaskDisplayStatus.Finished;
                if (_taskOriginalNames.TryGetValue(task.Id, out var originalName))
                {
                    task.Description = $"[cyan]{Markup.Escape(originalName)}[/]";
                }
            }
        }
    }

    private void OnPauseChanged(bool isPaused)
    {
        if (isPaused)
        {
            var now = Environment.TickCount64;
            var interval = (long)Components.AppSpinner.Interval.TotalMilliseconds;
            var count = Components.AppSpinner.Frames.Count;
            var frameIdx = (int)((now / interval) % count);

            foreach (var task in _progressTasks.Values)
            {
                _frozenFrames[task.Id] = frameIdx;
            }
        }
        else
        {
            _frozenFrames.Clear();
        }

        foreach (var task in _progressTasks.Values)
        {
            if (!_taskOriginalNames.TryGetValue(task.Id, out var originalName)) continue;

            if (isPaused && !task.IsFinished)
            {
                // Truncate filename so "PAUSED " (7 visible chars) + truncated == original width
                var maxNameLen = originalName.Length - 7;
                var truncated = maxNameLen >= 4
                    ? originalName[..(maxNameLen - 3)] + "..."
                    : maxNameLen > 0 ? originalName[..maxNameLen] : "";
                task.Description = $"[yellow]PAUSED[/] [cyan]{Markup.Escape(truncated)}[/]";
            }
            else
            {
                // Restore original name for unpaused or finished tasks
                task.Description = $"[cyan]{Markup.Escape(originalName)}[/]";
            }
        }
    }

    private async Task MonitorKeypressesAsync(Task downloadLoopTask, CancellationTokenSource linkedCts, Action onSaveAndExit)
    {
        while (!downloadLoopTask.IsCompleted)
        {
            if (linkedCts.Token.IsCancellationRequested) break;

            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.P)
                {
                    _downloader.TogglePause();
                }
                else if (key.Key == ConsoleKey.X)
                {
                    onSaveAndExit();
                    var now = Environment.TickCount64;
                    var interval = (long)Components.AppSpinner.Interval.TotalMilliseconds;
                    var count = Components.AppSpinner.Frames.Count;
                    var frameIdx = (int)((now / interval) % count);

                    foreach (var t in _progressTasks.Values)
                    {
                        if (t.IsFinished) continue;
                        _taskDisplayStatuses[t.Id] = TaskDisplayStatus.Saved;
                        _frozenFrames[t.Id] = frameIdx;
                        if (_taskOriginalNames.TryGetValue(t.Id, out var origName))
                        {
                            var maxNameLen = origName.Length - 7;
                            var truncated = maxNameLen >= 4
                                ? origName[..(maxNameLen - 3)] + "..."
                                : maxNameLen > 0 ? origName[..maxNameLen] : "";
                            t.Description = $"[blue]SAVED [/] [cyan]{Markup.Escape(truncated)}[/]";
                        }
                    }
                    linkedCts.Cancel();
                    break;
                }
            }

            try
            {
                await Task.Delay(100, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                foreach (var t in _progressTasks.Values)
                {
                    if (!t.IsFinished)
                    {
                        _taskDisplayStatuses.TryAdd(t.Id, TaskDisplayStatus.Cancelled);
                    }
                }
                break;
            }
        }
    }
}