using System.Text.RegularExpressions;

namespace MapCue.Application.Graph;

/// <summary>
/// Masks dynamic content in labels for identity (architecture §7.1 step 2): profile input values → <c>‹input›</c>, redaction markers
/// → <c>‹redacted›</c>, e-mails, IBANs, dates, times and amounts → tokens, any remaining number → <c>#</c> (a whole number, so
/// 7 and 12 mask the same). Whitespace is collapsed and the result is lower-cased (invariant). Part of fingerprint version 1.
/// Date patterns cover English and Polish month names only; other locales are calibration work (R-04).
/// </summary>
public static partial class TextMasker
{
    public const string InputToken = "‹input›";

    public static string? Mask(string? text, IReadOnlyCollection<string>? inputValues = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text;
        if (inputValues is { Count: > 0 })
        {
            foreach (var input in inputValues.Where(v => !string.IsNullOrEmpty(v)).OrderByDescending(v => v.Length).ThenBy(v => v, StringComparer.Ordinal))
            {
                value = value.Replace(input, InputToken, StringComparison.Ordinal);
            }
        }

        value = RedactionMarker().Replace(value, "‹redacted›");
        value = Email().Replace(value, "‹email›");
        value = Iban().Replace(value, "‹iban›");
        value = IsoDate().Replace(value, "‹date›");
        value = NumericDate().Replace(value, "‹date›");
        value = MonthNameDate().Replace(value, "‹date›");
        value = DayMonthNameDate().Replace(value, "‹date›");
        value = Time().Replace(value, "‹time›");
        value = AmountPrefix().Replace(value, "‹amount›");
        value = AmountSuffix().Replace(value, "‹amount›");
        value = Number().Replace(value, "#");
        value = Whitespace().Replace(value, " ").Trim().ToLowerInvariant();
        return value.Length == 0 ? null : value;
    }

    [GeneratedRegex(@"\[REDACTED:[a-z]+\]", RegexOptions.CultureInvariant)]
    private static partial Regex RedactionMarker();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[A-Z]{2}[0-9]{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,3})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex Iban();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b\d{1,2}[./]\d{1,2}[./]\d{2,4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex NumericDate();

    // "Sun, Oct 25", "October 25, 2026", "Oct 25".
    [GeneratedRegex(@"\b(?:(?:mon|tue|wed|thu|fri|sat|sun)[a-z]*\.?,?\s+)?(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+\d{1,2}(?:st|nd|rd|th)?(?:,?\s+\d{4})?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonthNameDate();

    // "25 Oct 2026", "25 października", "3 lip".
    [GeneratedRegex(@"\b\d{1,2}\.?\s+(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec|sty|lut|kwi|maj|cze|lip|sie|wrz|paź|paz|lis|gru)\p{L}*\.?(?:\s+\d{4})?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DayMonthNameDate();

    [GeneratedRegex(@"\b\d{1,2}:\d{2}(?::\d{2})?(?:\s?[AaPp]\.?[Mm]\.?)?", RegexOptions.CultureInvariant)]
    private static partial Regex Time();

    [GeneratedRegex(@"(?:[$€£¥₽]|\b(?:PLN|USD|EUR|GBP|CHF|zł)\b)\s?[-−]?\d(?:[\d\u00A0\u202F ,.]*\d)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmountPrefix();

    [GeneratedRegex(@"[-−]?\d(?:[\d\u00A0\u202F ,.]*\d)?\s?(?:[$€£¥₽]|(?:PLN|USD|EUR|GBP|CHF)\b|zł)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmountSuffix();

    [GeneratedRegex(@"\d+(?:[.,\u00A0\u202F]\d+)*", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
