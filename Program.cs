using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaDebrid_cli.Views;
using MediaDebrid_cli.Core;
using Spectre.Console;

namespace MediaDebrid_cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Settings.Load();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (!cts.IsCancellationRequested)
            {
                e.Cancel = true;
                cts.Cancel();
            }
        };

        Downloader.CleanupStaleFiles(Settings.Instance.MediaRoot);

        var rootCommand = new RootCommand("MediaDebrid — magnet-to-media downloader")
        {
            Name = "mediadebrid-cli"
        };

        // ── add Command ────────────────────────────────────────────────────
        var addCommand = new Command("add", "Add a magnet link and start downloading");
        var magnetArg = new Argument<string>("magnet") { Description = "Magnet link to process" };
        var typeOption = new Option<string?>("--type", "Media type (movie or show)");
        var titleOption = new Option<string?>("--title", "Title of the media");
        var yearOption = new Option<string?>("--year", "Year of release (optional)");
        var seasonOption = new Option<int?>("--season", "Season number (for shows)");

        addCommand.AddArgument(magnetArg);
        addCommand.AddOption(typeOption);
        addCommand.AddOption(titleOption);
        addCommand.AddOption(yearOption);
        addCommand.AddOption(seasonOption);

        addCommand.SetHandler(async (string magnet, string? type, string? title, string? year, int? season) =>
        {
            await EnsureConfiguredAsync(cts.Token);
            await RunAppAsync(magnet, type, title, year, season, showLogo: true, cts.Token);
        }, magnetArg, typeOption, titleOption, yearOption, seasonOption);

        rootCommand.AddCommand(addCommand);

        // ── set Command ────────────────────────────────────────────────────
        var setCommand = new Command("set", "Set a configuration value");
        var keyArg = new Argument<string>("key") { Description = "Configuration key (e.g. real_debrid_api_key)" };
        var valueArg = new Argument<string>("value") { Description = "Configuration value" };
        setCommand.AddArgument(keyArg);
        setCommand.AddArgument(valueArg);

        setCommand.SetHandler((string key, string value) =>
        {
            SetConfigurationValue(key, value);
        }, keyArg, valueArg);

        rootCommand.AddCommand(setCommand);

        // ── list Command ───────────────────────────────────────────────────
        var listCommand = new Command("list", "List all current configurations");
        listCommand.SetHandler(() =>
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(Settings.Instance, options);
            AnsiConsole.MarkupLine("[cyan]Current Configuration:[/]");
            Console.WriteLine(json);
        });

        rootCommand.AddCommand(listCommand);

        // ── Interactive mode (no args) ─────────────────────────────────────
        if (args.Length == 0)
        {
            try
            {
                await EnsureConfiguredAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            TuiApp.ShowLogo();

            string? magnet = null;
            while (!cts.Token.IsCancellationRequested)
            {
                string? input = null;
                try
                {
                    input = await CancellablePromptAsync(
                        new TextPrompt<string>("Enter [green]Magnet Link[/]:")
                            .PromptStyle("white")
                            .Validate(k => 
                            {
                                if (string.IsNullOrWhiteSpace(k)) return ValidationResult.Error("[red]Magnet link cannot be empty.[/]");
                                if (!k.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return ValidationResult.Error("[red]Invalid magnet link format.[/]");
                                if (Core.MagnetParser.ExtractHash(k) == null) return ValidationResult.Error("[red]Invalid magnet link: Missing BTIH hash (xt=urn:btih:).[/]");
                                return ValidationResult.Success();
                            }),
                        cts.Token
                    );
                }
                catch (OperationCanceledException)
                {
                    AnsiConsole.MarkupLine("\n[red]Application terminated. Exiting...[/]");
                    break;
                }

                if (input is null || cts.Token.IsCancellationRequested) break;

                magnet = input;
                break;
            }

            if (magnet is not null)
            {
                try
                {
                    await RunAppAsync(magnet, null, null, null, null, showLogo: false, cts.Token);
                }
                catch (OperationCanceledException) { }
            }

            return 0;
        }

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task EnsureConfiguredAsync(CancellationToken cancellationToken)
    {
        if (Settings.IsConfigured()) return;

        cancellationToken.ThrowIfCancellationRequested();

        TuiApp.ShowLogo();
        AnsiConsole.MarkupLine("\n[yellow]Initial Setup Required[/]");
        AnsiConsole.MarkupLine("Please provide the following required configuration values:\n");

        try
        {
            if (string.IsNullOrWhiteSpace(Settings.Instance.RealDebridApiToken))
            {
                Settings.Instance.RealDebridApiToken = await CancellablePromptAsync(
                    new TextPrompt<string>("Enter [green]Real-Debrid API Key[/]:")
                        .PromptStyle("white")
                        .Secret()
                        .Validate(k => string.IsNullOrWhiteSpace(k) ? ValidationResult.Error("[red]Key cannot be empty.[/]") : ValidationResult.Success()),
                    cancellationToken
                );
            }

            if (string.IsNullOrWhiteSpace(Settings.Instance.TmdbReadAccessToken))
            {
                Settings.Instance.TmdbReadAccessToken = await CancellablePromptAsync(
                    new TextPrompt<string>("Enter [green]TMDB Read Access Token[/]:")
                        .PromptStyle("white")
                        .Secret()
                        .Validate(k => string.IsNullOrWhiteSpace(k) ? ValidationResult.Error("[red]Token cannot be empty.[/]") : ValidationResult.Success()),
                    cancellationToken
                );
            }

            if (Settings.Instance.MediaRoot == "./media" || string.IsNullOrWhiteSpace(Settings.Instance.MediaRoot))
            {
                Settings.Instance.MediaRoot = await CancellablePromptAsync(
                    new TextPrompt<string>("Enter [green]Media Root Path[/]:")
                        .DefaultValue("./media")
                        .PromptStyle("white"),
                    cancellationToken
                );
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[red]Setup cancelled. Exiting...[/]");
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Settings.Save();
        AnsiConsole.MarkupLine("\n[green]Configuration saved successfully![/]\n");
    }

    private static async Task<T> CancellablePromptAsync<T>(IPrompt<T> prompt, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>();
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());

        _ = Task.Run(() =>
        {
            try
            {
                var result = AnsiConsole.Prompt(prompt);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, cancellationToken);

        return await tcs.Task;
    }

    private static void SetConfigurationValue(string key, string value)
    {
        var properties = typeof(Models.AppSettings).GetProperties();
        foreach (var prop in properties)
        {
            var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            var propName = attr != null ? attr.Name : prop.Name;

            if (propName.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    object convertedValue = Convert.ChangeType(value, prop.PropertyType);
                    prop.SetValue(Settings.Instance, convertedValue);
                    Settings.Save();
                    AnsiConsole.MarkupLine($"[green]Successfully updated '{key}' to '{value}'[/]");
                    return;
                }
                catch (Exception)
                {
                    AnsiConsole.MarkupLine($"[red]Failed to convert '{value}' to type {prop.PropertyType.Name} for key '{key}'[/]");
                    return;
                }
            }
        }
        
        AnsiConsole.MarkupLine($"[red]Configuration key '{key}' not found.[/]");
        AnsiConsole.MarkupLine("Available keys:");
        foreach (var prop in properties)
        {
            var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            var propName = attr != null ? attr.Name : prop.Name;
            AnsiConsole.MarkupLine($"- [cyan]{propName}[/] ({prop.PropertyType.Name})");
        }
    }

    private static async Task RunAppAsync(string magnet, string? type, string? title, string? year, int? season, bool showLogo, CancellationToken cancellationToken)
    {
        var app = new TuiApp();
        try
        {
            await app.RunAsync(magnet, type, title, year, season, showLogo: showLogo, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[red]Termination requested. Cleaning up...[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }
    }
}
