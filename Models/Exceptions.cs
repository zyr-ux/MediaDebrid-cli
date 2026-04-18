using Spectre.Console;

namespace MediaDebrid_cli.Models;

// Robust Polymorphic termination exception handling
public class TerminationException : OperationCanceledException
{
    private readonly string? _customMessage;
    private bool _wasPrinted;

    public TerminationException(string? customMessage = null) : base()
    {
        _customMessage = customMessage;
    }

    public void Print()
    {
        if (_wasPrinted) return;

        if (_customMessage != null)
        {
            if (!string.IsNullOrEmpty(_customMessage))
            {
                AnsiConsole.MarkupLine(_customMessage);
            }
        }
        else
        {
            AnsiConsole.MarkupLine("\n[red]Operation cancelled. Exiting...[/]");
        }

        _wasPrinted = true;
    }
}
