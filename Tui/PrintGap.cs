using Spectre.Console;
using Spectre.Console.Rendering;

namespace MediaDebrid_cli.Tui;


// A modular, self-contained console printer that automatically tracks and self-corrects vertical spacing gaps to prevent double empty lines.
public static class PrintGap
{
    private static bool _hasGap = true; // Initialize to true to suppress gaps at the absolute top
    
    // Gets or sets the gap state directly.
    public static bool HasGap
    {
        get => _hasGap;
        set => _hasGap = value;
    }

    /// Suppresses the next gap. Useful before or after padded components (like Progress bars).
    public static void Suppress()
    {
        _hasGap = true;
    }

    /// Prints a single empty line gap (newline) unless a gap is already present or has been suppressed.
    public static void Print()
    {
        if (_hasGap) return;

        AnsiConsole.WriteLine();
        _hasGap = true;
    }

    /// Prints a styled markup line and clears the gap state.
    public static void MarkupLine(string markup)
    {
        AnsiConsole.MarkupLine(markup);
        _hasGap = false;
    }

    /// Prints a styled markup line with arguments and clears the gap state.
    public static void MarkupLine(string format, params object[] args)
    {
        AnsiConsole.MarkupLine(format, args);
        _hasGap = false;
    }

    /// Prints styled markup without a newline and clears the gap state.
    public static void Markup(string markup)
    {
        AnsiConsole.Markup(markup);
        _hasGap = false;
    }

    /// Prints a Spectre.Console IRenderable (e.g. Panel, Table, Grid) and clears the gap state.
    public static void Write(IRenderable renderable)
    {
        AnsiConsole.Write(renderable);
        _hasGap = false;
    }
}
