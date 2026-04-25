using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using MediaDebrid_cli.Services;
using MediaDebrid_cli.Tui;
using MediaDebrid_cli.Models;
using Spectre.Console;

namespace MediaDebrid_cli;

// Helper class to show the logo before the default help output for the root command
// On parse errors, shows a short hint instead of the full help dump
internal sealed class LogoHelpAction(HelpAction defaultHelp) : SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        // Parse errors trigger help automatically — show hint instead of full help
        if (parseResult.Errors.Count > 0)
        {
            AnsiConsole.MarkupLine("[grey]Use 'mediadebrid --help' to get a list of all available commands.[/]");
            return 1;
        }

        if (parseResult.CommandResult.Command is RootCommand)
        {
            TuiApp.ShowLogo();
            Console.Write(Utils.GetRootHelpDescription());
            return 0;
        }

        return defaultHelp.Invoke(parseResult);
    }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Settings.Load();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return;
            e.Cancel = true;
            cts.Cancel();
        };



        var app = new TuiApp();

        var rootCommand = new RootCommand(Utils.GetRootHelpDescription());


        // ── unres Command ──────────────────────────────────────────────────
        var unresCommand = new Command("unres", "Generate unrestricted links instead of downloading");
        var unresMagnetArg = new Argument<string?>("magnet") { Description = "Optional magnet link to process", Arity = ArgumentArity.ZeroOrOne };

        unresCommand.Arguments.Add(unresMagnetArg);

        unresCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var magnet = parseResult.GetValue(unresMagnetArg);
            if (string.IsNullOrWhiteSpace(magnet))
            {
                await app.RunInteractiveAsync(cts.Token, generateUnresLinks: true);
            }
            else
            {
                await app.RunAsync(magnet, null, null, showLogo: true, cts.Token, forceResume: false, generateUnresLinks: true);
            }
        });

        rootCommand.Subcommands.Add(unresCommand);

        // ── set Command ────────────────────────────────────────────────────
        var setCommand = new Command("set", "Set a configuration value");
        var keyArg = new Argument<string>("key") { Description = "Configuration key" };
        var valueArg = new Argument<string>("value") { Description = "Configuration value" };
        setCommand.Arguments.Add(keyArg);
        setCommand.Arguments.Add(valueArg);

        setCommand.SetAction(parseResult =>
        {
            app.SetConfigurationValue(parseResult.GetValue(keyArg)!, parseResult.GetValue(valueArg)!);
        });

        rootCommand.Subcommands.Add(setCommand);

        // ── list Command ───────────────────────────────────────────────────
        var listCommand = new Command("list", "List all current configurations");
        listCommand.SetAction(_ =>
        {
            app.ListConfiguration();
        });

        rootCommand.Subcommands.Add(listCommand);
        
        // ── resume Command ──────────────────────────────────────────────────
        var resumeCommand = new Command("resume", "Resume a download from a .mdebrid file");
        var pathArg = new Argument<string>("path") { Description = "Path to the .mdebrid file" };
        resumeCommand.Arguments.Add(pathArg);
        resumeCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            await app.RunResumeAsync(parseResult.GetValue(pathArg)!, cts.Token);
        });
        
        rootCommand.Subcommands.Add(resumeCommand);

        // ── Customize help to show logo + description for root command ──
        for (int i = 0; i < rootCommand.Options.Count; i++)
        {
            if (rootCommand.Options[i] is HelpOption helpOption)
            {
                helpOption.Action = new LogoHelpAction((HelpAction)helpOption.Action!);
                break;
            }
        }

        // ── Execute ────────────────────────────────────────────────────────
        try
        {
            if (args.Length != 0) return await rootCommand.Parse(args).InvokeAsync();

            await app.RunInteractiveAsync(cts.Token);
            return 0;

        }
        catch (RealDebridApiException ex)
        {
            ex.Print();
            return 1;
        }
        catch (MagnetException ex)
        {
            ex.Print();
            return 1;
        }
        catch (ConfigurationException ex)
        {
            ex.Print();
            return 1;
        }
        catch (DownloadException ex)
        {
            ex.Print();
            return 1;
        }
        catch (RealDebridClientException ex)
        {
            ex.Print();
            return 1;
        }
        catch (OperationCanceledException ex)
        {
            var tex = ex as TerminationException ?? new TerminationException();
            tex.Print();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Unexpected error ({Markup.Escape(ex.GetType().Name)}):[/] [white]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
