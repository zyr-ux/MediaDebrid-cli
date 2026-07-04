using System.Text.RegularExpressions;

namespace MediaDebrid_cli.Features.Download_Manager;

/// <summary>
/// Utility methods for extracting fields from magnet URIs.
/// </summary>
public static class MagnetParser
{
    private static readonly Regex HashRegex =
        new(@"xt=urn:btih:([a-zA-Z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NameRegex =
        new(@"dn=([^&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns the info-hash from a magnet link, or <c>null</c> if not found.</summary>
    public static string? ExtractHash(string magnet)
    {
        var match = HashRegex.Match(magnet);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Returns the display name (<c>dn</c>) from a magnet link, or <c>null</c> if not found.</summary>
    public static string? ExtractName(string magnet)
    {
        var match = NameRegex.Match(magnet);
        if (!match.Success) return null;

        try
        {
            return Uri.UnescapeDataString(match.Groups[1].Value.Replace("+", " "));
        }
        catch
        {
            return match.Groups[1].Value;
        }
    }

    /// <summary>Validates a magnet link.</summary>
    public static (bool IsValid, string? ErrorMessage) Validate(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet))
            return (false, "Magnet link cannot be empty.");

        if (!magnet.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            return (false, "Invalid magnet link format. Must start with 'magnet:?'.");

        if (ExtractHash(magnet) == null)
            return (false, "Invalid magnet link: Missing BTIH hash (xt=urn:btih:).");

        return (true, null);
    }
}
