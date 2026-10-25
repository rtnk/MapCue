using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MapCue.Application.Configuration;

/// <summary>Parses durations of the form <c>500ms</c>, <c>30s</c>, <c>60m</c>, <c>1h</c>.</summary>
public static partial class DurationParser
{
    public static bool TryParse(string? text, [NotNullWhen(true)] out TimeSpan? duration)
    {
        duration = null;
        if (text is null)
        {
            return false;
        }

        var match = Pattern().Match(text);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            return false;
        }

        duration = match.Groups[2].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(amount),
            "s" => TimeSpan.FromSeconds(amount),
            "m" => TimeSpan.FromMinutes(amount),
            _ => TimeSpan.FromHours(amount),
        };
        return true;
    }

    [GeneratedRegex("^([0-9]{1,6})(ms|s|m|h)$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
