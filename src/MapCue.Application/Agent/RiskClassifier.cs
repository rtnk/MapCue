using System.Text;
using System.Text.RegularExpressions;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.Agent;

/// <summary>
/// An operator rule (architecture §9.3 step 4): an element matching <see cref="ResourceId"/> exactly, <see cref="ResourceIdPattern"/>
/// or <see cref="LabelPattern"/> gets at least <see cref="Risk"/>. Rules only raise the risk; nothing can lower it (R-06).
/// </summary>
public sealed record OperatorRiskRule(string Name, RiskClass Risk, string? ResourceId = null, Regex? ResourceIdPattern = null, Regex? LabelPattern = null)
{
    public bool Matches(string? resourceId, IEnumerable<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (ResourceId is not null && string.Equals(ResourceId, resourceId, StringComparison.Ordinal))
        {
            return true;
        }

        if (ResourceIdPattern is not null && resourceId is not null && SafeMatch(ResourceIdPattern, resourceId))
        {
            return true;
        }

        return LabelPattern is not null && texts.OfType<string>().Any(t => SafeMatch(LabelPattern, t));
    }

    // A rule that times out counts as a match: an operator rule never silently stops protecting (fail closed).
    private static bool SafeMatch(Regex pattern, string value)
    {
        try
        {
            return pattern.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }
}

/// <summary>Result of the rule-based classification, with the rules that fired (recorded with the decision).</summary>
public sealed record RiskAssessment(RiskClass Risk, IReadOnlyList<string> Reasons);

/// <summary>
/// Deterministic risk classifier (architecture §9.3 step 4, R-06). The base risk comes from the action type; keywords in
/// labels, texts and resource ids (English, Polish, German, French, Spanish, Italian, Portuguese; diacritics folded, word or
/// word-prefix match) and operator rules raise it. The effective risk of an agent proposal is <see cref="Max"/> of the agent's
/// class and this one, so the agent can only make an action riskier, never safer.
/// </summary>
public sealed class RiskClassifier(IReadOnlyList<OperatorRiskRule>? operatorRules = null)
{
    private readonly IReadOnlyList<OperatorRiskRule> _operatorRules = operatorRules ?? [];

    /// <summary>Riskier of two classes (the enum is ordered from least to most risky).</summary>
    public static RiskClass Max(RiskClass a, RiskClass b) => (RiskClass)Math.Max((int)a, (int)b);

    /// <summary>Classifies <paramref name="type"/> on <paramref name="element"/> (null for screen-level actions such as back).</summary>
    public RiskAssessment Classify(ActionType type, UiElement? element) =>
        element is null
            ? Classify(type, null, null, [])
            : Classify(type, element.Role, element.ResourceId, [element.Label, element.Text]);

    public RiskAssessment Classify(ActionType type, string? role, string? resourceId, IReadOnlyList<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var reasons = new List<string>();
        var risk = BaseRisk(type);
        reasons.Add($"action:{type}={risk}");

        if (role == "toggle" && type is ActionType.Tap or ActionType.LongPress)
        {
            risk = Raise(risk, RiskClass.StateChanging, "role:toggle", reasons);
        }

        // Keywords matter only when the action acts on the element (scrolling a list of "Delete" rows deletes nothing).
        if (type is ActionType.Tap or ActionType.LongPress or ActionType.InputText)
        {
            var words = Words([.. texts, IdWords(resourceId)]);
            foreach (var keyword in RiskKeywords.All)
            {
                if (keyword.Matches(words))
                {
                    risk = Raise(risk, keyword.Risk, "keyword:" + keyword.Text, reasons);
                }
            }
        }

        foreach (var rule in _operatorRules)
        {
            if (rule.Matches(resourceId, texts))
            {
                risk = Raise(risk, rule.Risk, "operator:" + rule.Name, reasons);
            }
        }

        return new RiskAssessment(risk, reasons);
    }

    private static RiskClass BaseRisk(ActionType type) => type switch
    {
        ActionType.Back or ActionType.Scroll or ActionType.Swipe or ActionType.HideKeyboard or ActionType.WaitForAnimationToEnd => RiskClass.Safe,
        ActionType.Tap => RiskClass.Navigational,
        ActionType.InputText => RiskClass.StateChanging,

        // Long press, point, links, permissions and launches are never classified as harmless by default.
        _ => RiskClass.Unknown,
    };

    private static RiskClass Raise(RiskClass current, RiskClass candidate, string reason, List<string> reasons)
    {
        if ((int)candidate > (int)current)
        {
            reasons.Add(reason);
            return candidate;
        }

        return current;
    }

    /// <summary>Resource ids as words: package prefix removed, camelCase, snake_case and kebab-case split.</summary>
    private static string? IdWords(string? resourceId)
    {
        if (string.IsNullOrEmpty(resourceId))
        {
            return null;
        }

        var name = resourceId;
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && char.IsLower(name[i - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(c is '_' or '-' or '.' ? ' ' : c);
        }

        return builder.ToString();
    }

    /// <summary>Folded words of all texts, as one sequence per text (a phrase never spans two texts).</summary>
    private static List<string[]> Words(IEnumerable<string?> texts) =>
        texts.OfType<string>().Select(t => TextFold.Words(t)).Where(w => w.Length > 0).ToList();
}

/// <summary>Lowercasing and diacritic folding that does not depend on ICU (the build uses invariant globalization).</summary>
public static class TextFold
{
    private static readonly Dictionary<char, string> _map = new()
    {
        ['ą'] = "a",
        ['ć'] = "c",
        ['ę'] = "e",
        ['ł'] = "l",
        ['ń'] = "n",
        ['ó'] = "o",
        ['ś'] = "s",
        ['ź'] = "z",
        ['ż'] = "z",
        ['ä'] = "a",
        ['ö'] = "o",
        ['ü'] = "u",
        ['ß'] = "ss",
        ['à'] = "a",
        ['á'] = "a",
        ['â'] = "a",
        ['ã'] = "a",
        ['å'] = "a",
        ['ç'] = "c",
        ['è'] = "e",
        ['é'] = "e",
        ['ê'] = "e",
        ['ë'] = "e",
        ['ì'] = "i",
        ['í'] = "i",
        ['î'] = "i",
        ['ï'] = "i",
        ['ñ'] = "n",
        ['ò'] = "o",
        ['ô'] = "o",
        ['õ'] = "o",
        ['ù'] = "u",
        ['ú'] = "u",
        ['û'] = "u",
        ['ý'] = "y",
        ['ÿ'] = "y",
        ['’'] = "'",
    };

    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (_map.TryGetValue(c, out var replacement))
            {
                builder.Append(replacement);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Folded words (letters and digits only; apostrophes split words).</summary>
    public static string[] Words(string text)
    {
        var folded = Fold(text);
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in folded)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return [.. words];
    }
}
