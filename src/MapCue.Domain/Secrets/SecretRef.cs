using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace MapCue.Domain.Secrets;

/// <summary>
/// Reference to a secret. The only supported form is <c>secret://env/NAME</c> (architecture §10.3);
/// the value is resolved at execution time and never stored in configuration or artifacts.
/// </summary>
public sealed partial record SecretRef
{
    public const string EnvPrefix = "secret://env/";

    private SecretRef(string name) => Name = name;

    /// <summary>Environment variable name (upper case, digits, underscores).</summary>
    public string Name { get; }

    public string Uri => EnvPrefix + Name;

    public static bool IsSecretUri(string? value) =>
        value is not null && value.StartsWith("secret://", StringComparison.Ordinal);

    public static bool TryParse(string? value, [NotNullWhen(true)] out SecretRef? secretRef)
    {
        secretRef = null;
        if (value is null || !value.StartsWith(EnvPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var name = value[EnvPrefix.Length..];
        if (!EnvNamePattern().IsMatch(name))
        {
            return false;
        }

        secretRef = new SecretRef(name);
        return true;
    }

    public static SecretRef Parse(string value) =>
        TryParse(value, out var r) ? r : throw new FormatException("Secret reference must have the form secret://env/NAME.");

    public override string ToString() => Uri;

    [GeneratedRegex("^[A-Z_][A-Z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvNamePattern();
}
