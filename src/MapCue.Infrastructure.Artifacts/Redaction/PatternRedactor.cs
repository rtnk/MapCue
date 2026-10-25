using System.Text;
using System.Text.RegularExpressions;
using MapCue.Application.Ports;

namespace MapCue.Infrastructure.Artifacts.Redaction;

/// <summary>
/// Default redactor (architecture §10.3): known sensitive values first (longest first), then patterns
/// for bearer tokens, JWTs, password-like fields, e-mails, IBANs, card numbers (Luhn) and phone numbers.
/// Thread-safe.
/// </summary>
public sealed partial class PatternRedactor : IRedactor
{
    private readonly Lock _gate = new();
    private string[] _knownValues = [];

    public static string Marker(string rule) => $"[REDACTED:{rule}]";

    public void RegisterSensitiveValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        lock (_gate)
        {
            if (Array.IndexOf(_knownValues, value) >= 0)
            {
                return;
            }

            _knownValues = [.. _knownValues.Append(value).OrderByDescending(v => v.Length).ThenBy(v => v, StringComparer.Ordinal)];
        }
    }

    public RedactedContent Redact(string content, RedactionContext context)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);

        var count = 0;
        var rules = new SortedSet<string>(StringComparer.Ordinal);
        var text = content;

        var known = Volatile.Read(ref _knownValues);
        foreach (var value in known)
        {
            var hits = CountOccurrences(text, value);
            if (hits > 0)
            {
                text = text.Replace(value, Marker("secret"), StringComparison.Ordinal);
                count += hits;
                rules.Add("secret");
            }
        }

        text = Apply(text, BearerPattern(), "bearer", ref count, rules);
        text = Apply(text, JwtPattern(), "jwt", ref count, rules);
        text = ApplyKeyValue(text, ref count, rules);
        text = Apply(text, EmailPattern(), "email", ref count, rules);
        text = Apply(text, IbanPattern(), "iban", ref count, rules);
        text = ApplyCardNumbers(text, ref count, rules);
        text = Apply(text, PhonePattern(), "phone", ref count, rules);

        return new RedactedContent(text, count, [.. rules]);
    }

    private static int CountOccurrences(string text, string value)
    {
        var hits = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            hits++;
            index += value.Length;
        }

        return hits;
    }

    private static string Apply(string text, Regex pattern, string rule, ref int count, SortedSet<string> rules)
    {
        var hits = 0;
        var result = pattern.Replace(text, _ =>
        {
            hits++;
            return Marker(rule);
        });
        if (hits > 0)
        {
            count += hits;
            rules.Add(rule);
        }

        return result;
    }

    private static string ApplyKeyValue(string text, ref int count, SortedSet<string> rules)
    {
        var hits = 0;
        var result = KeyValuePattern().Replace(text, m =>
        {
            if (m.Groups["value"].Value.StartsWith("[REDACTED:", StringComparison.Ordinal))
            {
                return m.Value;
            }

            hits++;
            return m.Groups["key"].Value + Marker("credential");
        });
        if (hits > 0)
        {
            count += hits;
            rules.Add("credential");
        }

        return result;
    }

    private static string ApplyCardNumbers(string text, ref int count, SortedSet<string> rules)
    {
        var hits = 0;
        var result = CardPattern().Replace(text, m =>
        {
            if (!PassesLuhn(m.Value))
            {
                return m.Value;
            }

            hits++;
            return Marker("pan");
        });
        if (hits > 0)
        {
            count += hits;
            rules.Add("pan");
        }

        return result;
    }

    private static bool PassesLuhn(string candidate)
    {
        var digits = new StringBuilder(candidate.Length);
        foreach (var c in candidate)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
        }

        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    [GeneratedRegex("""(?<key>["']?\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|client[_-]?secret)["']?\s*[:=]\s*)(?!//)(?<value>"[^"]*"|'[^']*'|[^\s,;&]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValuePattern();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b[A-Z]{2}[0-9]{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,3})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex IbanPattern();

    [GeneratedRegex(@"\b[0-9](?:[ -]?[0-9]){12,18}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CardPattern();

    [GeneratedRegex(@"\+[0-9]{1,3}(?:[ -]?[0-9]{2,4}){2,5}\b", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();
}
