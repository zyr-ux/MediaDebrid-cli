using System.Collections.Concurrent;
using MediaDebrid_cli.Models;
using Spectre.Console;
using MediaDebrid_cli.Services;
using Spectre.Console.Rendering;

namespace MediaDebrid_cli.Tui;

public class TuiApp
{
    private RealDebridClient? _client;
    private readonly Downloader _downloader;
    private readonly MetadataResolver _metadataResolver;

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

    private RealDebridClient GetClient() => _client ??= new RealDebridClient();

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
            await EnsureConfiguredAsync(cancellationToken);
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
                .StartAsync("Checking Real-Debrid cache...", async ctx =>
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
        catch (HttpRequestException ex)
        {
            throw new TerminationException($"[bold red]X[/] Network error during cache check: [white]{Markup.Escape(ex.Message)}[/]");
        }

        AnsiConsole.WriteLine();
        if (!newlyAdded)
        {
            // Existing torrent confirmed.
            AnsiConsole.MarkupLine($"[bold green]✓[/] Found existing torrent.");
        }
        else
        {
            // Newly added — cache status is unknown until after file selection.
            AnsiConsole.MarkupLine($"[bold green]✓[/] Magnet added to Real-Debrid.");
        }

        await AnsiConsole.Status()
            .StartAsync("Initializing...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Arc);
                ctx.SpinnerStyle(Style.Parse("green"));

                void ApplyOverrides(MediaMetadata meta) => Utils.ApplyMetadataOverrides(meta, seasonOverride, episodeOverride);

                try
                {
                    var magnetName = MagnetParser.ExtractName(magnet);
                    if (!string.IsNullOrEmpty(magnetName))
                    {
                        ctx.Status("[yellow]Resolving metadata from magnet...[/]");
                        resolved = await _metadataResolver.ResolveAsync(magnetName, cancellationToken: cancellationToken);
                        ApplyOverrides(resolved);
                        resolved.Destination = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, resolved.Season);
                    }

                    if (string.IsNullOrEmpty(torrentId))
                    {
                        ctx.Status("[yellow]Submitting magnet to Real-Debrid...[/]");
                        var addRes = await GetClient().AddMagnetAsync(magnet, cancellationToken: cancellationToken);
                        torrentId = addRes.Id;
                        AnsiConsole.MarkupLine($"[bold green]✓[/] Magnet submitted. RD ID: [cyan]{torrentId}[/]");
                    }

                    ctx.Status("[yellow]Fetching torrent info...[/]");
                    info = await GetClient().GetTorrentInfoAsync(torrentId, cancellationToken: cancellationToken);

                    if (resolved == null)
                    {
                        ctx.Status("[yellow]Resolving metadata from Real-Debrid filename...[/]");
                        resolved = await _metadataResolver.ResolveAsync(info.Filename, cancellationToken: cancellationToken);
                        ApplyOverrides(resolved);
                        resolved.Destination = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, resolved.Season);
                    }

                    ctx.Status("[yellow]Waiting for Real-Debrid status...[/]");
                    info = await GetClient().WaitForStatusAsync(torrentId, ["waiting_files_selection", "downloaded", "dead"
                    ], cancellationToken);

                }
                catch (TerminationException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (RealDebridApiException) { throw; }
                catch (HttpRequestException ex)
                {
                    throw new TerminationException($"[red]X[/] Network error during initialization: [white]{Markup.Escape(ex.Message)}[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error during initialization:[/] [white]{Markup.Escape(ex.Message)}[/]");
                }
            });

        if (info == null || resolved == null) return;

        bool needsNewline = true;

        if (info.Status == "dead")
        {
            throw new TerminationException("[bold red]X[/] Torrent is dead.");
        }

        List<int> seasonsInTorrent = [];
        if (resolved.Type == "show")
        {
            seasonsInTorrent = Utils.GetAvailableSeasons(info.Files, _metadataResolver);

            if (seasonsInTorrent.Count > 1 && string.IsNullOrEmpty(seasonOverride))
            {
                // Display accurate intent for all-seasons mode
                resolved.Season = "Multiple";
                var defaultSeasonDir = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, 1);
                resolved.Destination = Directory.GetParent(defaultSeasonDir)?.FullName ?? defaultSeasonDir;
            }
        }

        if (needsNewline) { AnsiConsole.WriteLine(); needsNewline = false; }
        Components.RenderMetadataPanel(resolved);
        AnsiConsole.WriteLine();

        // Interactive season selection for multi-season shows
        if (resolved.Type == "show" && string.IsNullOrEmpty(seasonOverride) && seasonsInTorrent.Count > 1)
        {
            try
            {
                string? input = null;
                var seasonRangeSuggestion = seasonsInTorrent.Count > 0 ? $"{seasonsInTorrent.Min()}-{seasonsInTorrent.Max()}" : "1-3";
                while (!cancellationToken.IsCancellationRequested)
                {
                    input = await ReadLineWithEffectAsync($"[yellow]Multiple seasons detected ({string.Join(", ", seasonsInTorrent.Select(s => $"S{s:D2}"))}).[/]\nEnter [green]season number or range[/] (e.g. {seasonRangeSuggestion}) to download (leave empty for all)", cancellationToken);
                    
                    if (cancellationToken.IsCancellationRequested) break;
                    if (string.IsNullOrWhiteSpace(input)) break;

                    var parsed = Utils.ParseRange(input);
                    if (parsed.Count == 0)
                    {
                        AnsiConsole.MarkupLine($"[red]Please enter a valid season number or range (e.g., {seasonRangeSuggestion}).[/]");
                        continue;
                    }
                    
                    if (!parsed.Any(s => seasonsInTorrent.Contains(s)))
                    {
                        AnsiConsole.MarkupLine($"[red]None of the specified seasons ({input}) were found in this torrent.[/]");
                        continue;
                    }
                    break;
                }

                if (!string.IsNullOrWhiteSpace(input))
                {
                    seasonOverride = input;
                    resolved.Season = input;
                    resolved.Destination = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year); // Generic dir for ranges
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"[bold green]✓[/] Selected seasons [cyan]{input}[/].");
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
            var episodesInTorrent = Utils.GetAvailableEpisodes(info.Files, sRange, _metadataResolver);

            if (episodesInTorrent.Count == 1)
            {
                if (needsNewline) { AnsiConsole.WriteLine(); needsNewline = false; }
                episodeOverride = episodesInTorrent[0].ToString();
                resolved.Episode = episodeOverride;
                AnsiConsole.MarkupLine($"[bold green]✓[/] Only one episode detected ([cyan]E{episodesInTorrent[0]:D2}[/]). Auto-selecting.");
            }
            else
            {
                if (needsNewline) { AnsiConsole.WriteLine(); needsNewline = false; }
                try
                {
                    string? input = null;
                    var epRangeSuggestion = episodesInTorrent.Count > 0 ? $"{episodesInTorrent.Min()}-{episodesInTorrent.Max()}" : "1-12";
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        input = await ReadLineWithEffectAsync($"Enter [green]episode number or range[/] (e.g. {epRangeSuggestion}) to download (leave empty for all)", cancellationToken);
                        
                        if (cancellationToken.IsCancellationRequested) break;
                        if (string.IsNullOrWhiteSpace(input)) break;

                        var parsed = Utils.ParseRange(input);
                        if (parsed.Count == 0)
                        {
                            AnsiConsole.MarkupLine($"[red]Please enter a valid episode number or range (e.g., {epRangeSuggestion}).[/]");
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
                            AnsiConsole.MarkupLine($"[red]No episodes from range {input} found {scope}.[/]");
                            continue;
                        }
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(input))
                    {
                        episodeOverride = input;
                        resolved.Episode = input;
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine($"[bold green]✓[/] Selected episodes [cyan]{input}[/].");
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
            selectedSeasons = Utils.DetermineSelectedSeasons(info.Files, seasonOverride, resolved.Season, _metadataResolver);
        }

        await AnsiConsole.Status()
            .StartAsync("Preparing selection...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Arc);
                ctx.SpinnerStyle(Style.Parse("green"));

                try
                {
                    var validation = Utils.ValidateExistingFiles(resolved, info, selectedSeasons, seasonOverride, episodeOverride);
                    existingEpisodeKeys = validation.ExistingEpisodeKeys;
                    
                    if (validation.AllSkipped)
                    {
                        throw new TerminationException(validation.ErrorMessage);
                    }
                    if (!string.IsNullOrEmpty(validation.WarnMessage))
                    {
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine(validation.WarnMessage);
                    }

                    if (info.Status == "waiting_files_selection")
                    {
                        ctx.Status("[yellow]Selecting files...[/]");
                        var fileIds = Utils.GetSelectedFiles(info.Files, seasonOverride, episodeOverride, existingEpisodeKeys);

                        if (fileIds.Length == 0)
                        {
                            throw new TerminationException("[bold red]X[/] No files found to download.");
                        }

                        await GetClient().SelectFilesAsync(torrentId, string.Join(",", fileIds), cancellationToken: cancellationToken);
                    }
                }
                catch (TerminationException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (RealDebridApiException) { throw; }
                catch (HttpRequestException ex)
                {
                    throw new TerminationException($"[red]X[/] Network error during initialization: [white]{Markup.Escape(ex.Message)}[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error during initialization:[/] [white]{Markup.Escape(ex.Message)}[/]");
                }
            });

        if (info == null || resolved == null) return;

        // Definitive cache check: after file selection the status is authoritative.
        // "downloaded" = cached. Anything else (downloading, queued, etc.) = not cached.
        info = await GetClient().GetTorrentInfoAsync(torrentId, cancellationToken);
        bool wasUncached = info.Status != "downloaded";
        if (wasUncached)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold yellow]![/] Magnet is [bold yellow]not cached[/] on Real-Debrid servers.");
            AnsiConsole.WriteLine();
            
            bool userCancelled = false;
            await AnsiConsole.Status()
                .StartAsync("File is being cached by Real-Debrid [dim][[press N to stop]][/]...", async ctx =>
                {
                    ctx.Spinner(Spinner.Known.Arc);
                    ctx.SpinnerStyle(Style.Parse("yellow"));

                    using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    
                    var pollTask = Task.Run(async () => 
                    {
                        try
                        {
                            info = await GetClient().WaitForStatusAsync(torrentId, ["downloaded", "dead"], pollCts.Token, 5000);
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
                throw new TerminationException("[red]Caching stopped by user. Magnet removed from Real-Debrid account.[/]");
            }
            
            if (info.Status == "dead")
            {
                throw new TerminationException("[bold red]X[/] Torrent is dead.");
            }
        }

        if (!wasUncached) AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold green]✓[/] Files are ready and [bold cyan]cached[/]!");

        if (generateUnresLinks)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Generating Unrestricted Links...[/]");
            AnsiConsole.WriteLine();
            
            await AnsiConsole.Status().StartAsync("[yellow]Unrestricting links...[/]", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Arc);
                var unrestrictedLinks = await GetClient().UnrestrictLinksAsync(info.Links, cancellationToken);
                foreach (var unrestricted in unrestrictedLinks)
                {
                    AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(unrestricted.Filename)}[/]");
                    AnsiConsole.MarkupLine($"[white]{unrestricted.Download}[/]");
                    AnsiConsole.WriteLine();
                }
            });
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Starting Downloads...[/]");
        AnsiConsole.MarkupLine("[dim]Controls: [yellow]P[/] Pause | [green]X[/] Save & Exit | [red]Ctrl+C[/] Cancel & Delete[/]");

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
        needsNewline = true;
        for (int i = 0; i < queuedDownloads.Count; i++)
        {
            var (unrestricted, resumeData, destPath) = queuedDownloads[i];
            if (resumeData != null)
            {
                if (!forceResume && needsNewline) { AnsiConsole.WriteLine(); needsNewline = false; }
                if (forceResume || await ConfirmAsync($"[yellow]Partial download found for {Markup.Escape(unrestricted.Filename)} ({Utils.FormatBytes(resumeData.Segments.Sum(s => s.Current - s.Start))} / {Utils.FormatBytes(resumeData.TotalSize)}). Resume?[/]", cancellationToken))
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
            var typeLabel = resolved.Type switch {
                "show" => "episodes",
                "movie" => "files",
                "game" => "files",
                _ => "files"
            };
            throw new TerminationException($"[bold red]All selected {typeLabel} already exist in your local library.[/]");
        }

        try
        {
            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new SpinnerColumn(_taskDisplayStatuses, _frozenFrames, _downloader),
                    new EpisodeColumn(_taskEpisodeTexts),
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn { Width = 200 },
                    new PercentageColumn(),
                    new CustomDownloadedColumn(),
                    new CustomTransferSpeedColumn(_taskSpeeds),
                    new CustomEtaColumn(_taskSpeeds))
                .StartAsync(async ctx =>
                {

                    downloadLoopTask = Task.Run(async () =>
                    {
                        foreach (var item in queuedDownloads)
                        {
                            var (unrestricted, resumeData, destPath) = item;
                            var currentResumeData = resumeData;

                            if (linkedCts.Token.IsCancellationRequested) break;

                            ProgressTask? progressTask = null;
                            try
                            {
                                var filename = unrestricted.Filename;
                                var tempPath = destPath + ".mdebrid";
                                
                                // Final safety check: Skip if file already exists
                                if (File.Exists(destPath))
                                {
                                    continue;
                                }

                                if (resolved.Type == "show" && Utils.IsEpisodeExisting(filename, existingEpisodeKeys, selectedSeasons))
                                {
                                    continue;
                                }

                                var progressKey = destPath;
                                activePaths.Add(tempPath);

                                var displayFilename = filename.Length > 40 ? filename[..37] + "..." : filename;

                                progressTask = ctx.AddTask($"[cyan]{Markup.Escape(displayFilename)}[/]", new ProgressTaskSettings { AutoStart = false });
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

                                progressTask.StartTask();

                                var rootPath = Settings.GetRootPathForType(resolved.Type);
                                
                                if (currentResumeData == null)
                                {
                                    currentResumeData = new ResumeMetadata
                                    {
                                        MagnetUri = magnet,
                                        FileId = unrestricted.Id,
                                        TotalSize = 0, // Will be set by downloader
                                        SeasonOverride = seasonOverride,
                                        EpisodeOverride = episodeOverride
                                    };
                                }
                                else
                                {
                                    // Initialize task with existing progress
                                    if (currentResumeData.TotalSize > 0)
                                    {
                                        progressTask.MaxValue = currentResumeData.TotalSize;
                                        progressTask.Value = currentResumeData.Segments.Sum(s => s.Current - s.Start);
                                    }
                                }

                                await _downloader.DownloadFileAsync(unrestricted.Download, destPath, rootPath, progressKey, linkedCts.Token, currentResumeData);

                                progressTask.Value = progressTask.MaxValue;
                                progressTask.StopTask();
                            }
                            catch (OperationCanceledException)
                            {
                                progressTask?.StopTask();
                                break;
                            }
                            catch (Exception ex)
                            {
                                shouldDeletePartial = false;
                                if (progressTask != null)
                                {
                                    _taskDisplayStatuses[progressTask.Id] = TaskDisplayStatus.Cancelled;
                                    progressTask.StopTask();
                                }
                                
                                // Mark all other active tasks as cancelled to ensure UI correctly reflects failure
                                foreach (var t in _progressTasks.Values)
                                {
                                    if (!t.IsFinished)
                                    {
                                        _taskDisplayStatuses.TryAdd(t.Id, TaskDisplayStatus.Cancelled);
                                    }
                                }

                                throw new DownloadException($"Download failed: {ex.Message}", ex);
                            }
                        }
                    }, cancellationToken);

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
                                shouldDeletePartial = false;
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
                                        var truncated = origName.Length > 33 ? origName[..30] + "..." : origName;
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
                    
                    if (downloadLoopTask != null) await downloadLoopTask;

                });

            if (!linkedCts.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("[bold green]All downloads completed![/]");
                shouldDeletePartial = false;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (MagnetException) { throw; }
        catch (ConfigurationException) { throw; }
        catch (DownloadException) { throw; }
        catch (RealDebridClientException) { throw; }
        catch (RealDebridApiException) { throw; }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Unexpected error ({Markup.Escape(ex.GetType().Name)}):[/] [white]{Markup.Escape(ex.Message)}[/]");
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
                AnsiConsole.MarkupLine("[red]Download cancelled. Cleaning up partial files...[/]");
                var cleanupRoot = resolved != null ? Settings.GetRootPathForType(resolved.Type) : null;
                Downloader.CleanupFiles(activePaths, cleanupRoot, force: true);
            }
            else if (linkedCts.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("[yellow]Stopping... Partial progress preserved for resume.[/]");
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
            await EnsureConfiguredAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Components.ShowLogo();

        string? magnet = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            magnet = await ReadLineWithEffectAsync("Enter [green]Magnet Link[/]: ", cancellationToken, ConsoleColor.Green);

            if (cancellationToken.IsCancellationRequested) break;

            var validation = MagnetParser.Validate(magnet);
            if (!validation.IsValid)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(validation.ErrorMessage!)}[/]");
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
            AnsiConsole.MarkupLine($"[red]Error:[/] File [cyan]{path}[/] not found.");
            return;
        }

        var metadata = _downloader.ReadResumeMetadata(path);
        if (metadata == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Could not read resume metadata from [cyan]{path}[/].");
            return;
        }

        await RunAsync(metadata.MagnetUri, metadata.SeasonOverride, metadata.EpisodeOverride, showLogo: true, forceResume: true, cancellationToken: cancellationToken);
    }

    public async Task EnsureConfiguredAsync(CancellationToken cancellationToken)
    {
        if (Settings.IsConfigured()) return;

        cancellationToken.ThrowIfCancellationRequested();

        AnsiConsole.MarkupLine("[yellow]Initial Setup Required[/]");
        AnsiConsole.MarkupLine("Please provide the following required configuration values:");
        AnsiConsole.WriteLine();

        try
        {
            if (string.IsNullOrWhiteSpace(Settings.Instance.RealDebridApiToken))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var token = await ReadLineWithEffectAsync("Enter [green]Real-Debrid API Key[/]", cancellationToken, secret: true);
                    if (cancellationToken.IsCancellationRequested) break;
                    
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        AnsiConsole.MarkupLine("[red]Key cannot be empty.[/]");
                        continue;
                    }
                    Settings.Instance.RealDebridApiToken = token;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(Settings.Instance.MediaRoot))
            {
                var root = await ReadLineWithEffectAsync("Enter [green]Movies/Shows Root Path[/]", cancellationToken, defaultValue: Settings.DefaultBaseRoot);
                Settings.Instance.MediaRoot = string.IsNullOrWhiteSpace(root) ? Settings.DefaultBaseRoot : root;
            }

            if (string.IsNullOrWhiteSpace(Settings.Instance.GamesRoot))
            {
                var root = await ReadLineWithEffectAsync("Enter [green]Games Root Path[/]", cancellationToken, defaultValue: Settings.DefaultBaseRoot);
                Settings.Instance.GamesRoot = string.IsNullOrWhiteSpace(root) ? Settings.DefaultBaseRoot : root;
            }

            if (string.IsNullOrWhiteSpace(Settings.Instance.OthersRoot))
            {
                var root = await ReadLineWithEffectAsync("Enter [green]Miscellaneous Downloads Root Path[/]", cancellationToken, defaultValue: Settings.DefaultBaseRoot);
                Settings.Instance.OthersRoot = string.IsNullOrWhiteSpace(root) ? Settings.DefaultBaseRoot : root;
            }
        }
        catch (OperationCanceledException)
        {
            var ex = new TerminationException("[red]Setup cancelled. Exiting...[/]");
            ex.Print();
            throw ex;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Settings.Save();
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Configuration saved successfully![/]");
        AnsiConsole.WriteLine();
    }

    private static async Task<bool> ConfirmAsync(string prompt, CancellationToken ct, bool defaultValue = true)
    {
        var choice = defaultValue ? "[[y/n]] (y)" : "[[y/n]] (n)";

        while (!ct.IsCancellationRequested)
        {
            var result = await ReadLineWithEffectAsync($"{prompt} [green]{choice}[/]: ", ct);
            if (ct.IsCancellationRequested) break;

            if (string.IsNullOrWhiteSpace(result)) return defaultValue;

            var trimmed = result.Trim().ToLowerInvariant();
            if (trimmed is "y" or "yes") return true;
            if (trimmed is "n" or "no") return false;

            AnsiConsole.MarkupLine("[red]Please enter 'y' or 'n'.[/]");
        }

        throw new OperationCanceledException(ct);
    }

    private static async Task<string?> ReadLineWithEffectAsync(string prompt, CancellationToken ct, ConsoleColor color = ConsoleColor.White, int batchSize = 5, bool secret = false, string? defaultValue = null)
    {
        var displayPrompt = prompt.Trim();
        if (!string.IsNullOrEmpty(defaultValue))
        {
            displayPrompt = $"{displayPrompt} [dim](leave blank for default)[/]";
        }
        
        if (!displayPrompt.EndsWith(':')) displayPrompt += ":";
        AnsiConsole.Markup(displayPrompt + " ");
        
        var sb = new System.Text.StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                {
                    AnsiConsole.WriteLine();
                    var result = sb.ToString().Trim();
                    return string.IsNullOrEmpty(result) ? defaultValue : result;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                    {
                        sb.Remove(sb.Length - 1, 1);
                        
                        // Handle manual wrap-around for backspace at the left edge
                        if (Console.CursorLeft == 0)
                        {
                            if (Console.CursorTop > 0)
                            {
                                // Move to end of previous line
                                Console.SetCursorPosition(Console.WindowWidth - 1, Console.CursorTop - 1);
                                Console.Write(" ");
                                Console.SetCursorPosition(Console.WindowWidth - 1, Console.CursorTop);
                            }
                        }
                        else
                        {
                            // Standard backspace within the same line
                            Console.Write("\b \b");
                        }
                    }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    
                    Console.ForegroundColor = color;
                    Console.Write(secret ? "*" : key.KeyChar);
                    Console.ResetColor();

                    if (Console.KeyAvailable && sb.Length % batchSize == 0)
                    {
                        // Batch delay to keep the speed high but the "filling in" effect visible
                        await Task.Delay(1, ct);
                    }
                }
            }
            else
            {
                // Yield to keep UI responsive and avoid CPU pinning
                await Task.Delay(5, ct);
            }
        }

        return null;
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
                // Truncate filename to 33 chars to fit "PAUSED " (7 chars) within the 40 char limit
                var truncated = originalName.Length > 33 ? originalName[..30] + "..." : originalName;
                task.Description = $"[yellow]PAUSED[/] [cyan]{Markup.Escape(truncated)}[/]";
            }
            else
            {
                // Restore original name for unpaused or finished tasks
                task.Description = $"[cyan]{Markup.Escape(originalName)}[/]";
            }
        }
    }
}