using System.Globalization;
using System.Text;

namespace MapCue.Adapters.TapCue.Flows;

/// <summary>
/// Escaping for values placed into generated flows. Maestro matches <c>text</c> and <c>id</c> selectors as a
/// full-string Java regex (case-insensitive for text), so literal values are regex-escaped first; the result is
/// then written as a YAML double-quoted scalar. Regex escaping also turns <c>${</c> into <c>\$\{</c>, so a value
/// taken from the screen can never become a JavaScript expression.
/// </summary>
public static class MaestroText
{
    private const string RegexSpecials = "\\^$.|?*+()[]{}";

    public static string EscapeRegex(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        var builder = new StringBuilder(literal.Length + 8);
        foreach (var c in literal)
        {
            if (RegexSpecials.Contains(c, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Returns a YAML double-quoted scalar (including the quotes) that decodes to exactly <paramref name="value"/>.</summary>
    public static string YamlQuote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (Rune.IsControl(rune) || rune.Value is 0x85 or 0x2028 or 0x2029 or 0xFEFF)
                    {
                        builder.Append("\\u").Append(rune.Value.ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(rune.ToString());
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}
