using Spectre.Console;

namespace MediaDebrid_cli.Models;

// Robust Polymorphic termination exception handling
public class TerminationException(string? customMessage = null) : OperationCanceledException
{
    private bool _wasPrinted;

    public void Print()
    {
        if (_wasPrinted) return;

        // Skip newline for empty custom messages (silent exit)
        if (customMessage != null && string.IsNullOrEmpty(customMessage))
        {
            _wasPrinted = true;
            return;
        }

        AnsiConsole.WriteLine();

        if (customMessage != null)
        {
            AnsiConsole.MarkupLine(customMessage);
        }
        else
        {
            AnsiConsole.MarkupLine("[red]Operation cancelled. Exiting...[/]");
        }

        _wasPrinted = true;
    }
}

public class RealDebridApiException : HttpRequestException
{
    public string Error { get; }
    public int ErrorCode { get; }

    public RealDebridApiException(string error, int errorCode, System.Net.HttpStatusCode statusCode) 
        : base($"Real-Debrid API Error: {error} (Code: {errorCode})", null, statusCode)
    {
        Error = error;
        ErrorCode = errorCode;
    }

    public void Print()
    {
        AnsiConsole.WriteLine();
        string msg = ErrorCode == 35 
            ? "[bold red]X[/] Real-Debrid has blocked this magnet as an infringing file (Code 35)."
            : $"[bold red]X[/] Real-Debrid API Error: [white]{Markup.Escape(Error)}[/] (Code: {ErrorCode})";
        AnsiConsole.MarkupLine(msg);
    }
}

public class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message) { }

    public void Print()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold red]X[/] Configuration Error: [white]{Markup.Escape(Message)}[/]");
    }
}

public class MagnetException : Exception
{
    public MagnetException(string message) : base(message) { }

    public void Print()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold red]X[/] Magnet Error: [white]{Markup.Escape(Message)}[/]");
    }
}

public class DownloadException : Exception
{
    public DownloadException(string message, Exception? innerException = null) 
        : base(message, innerException) { }

    public void Print()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold red]X[/] Download Error: [white]{Markup.Escape(Message)}[/]");
    }
}

public class RealDebridClientException : Exception
{
    public RealDebridClientException(string message, Exception? innerException = null) 
        : base(message, innerException) { }

    public void Print()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold red]X[/] Client Error: [white]{Markup.Escape(Message)}[/]");
    }
}
