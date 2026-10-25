using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MapCue.Adapters.TapCue.Flows;

/// <summary>
/// Fail-closed policy check of a flow before TapCue sees it (architecture §10.1 layer 4,
/// docs/tapcue-compatibility.md §2.3). Rejects forbidden commands anywhere in the flow (including nested
/// <c>repeat</c>/<c>retry</c> blocks), flow hooks, unknown header keys and any <c>${...}</c> expression other
/// than a plain environment variable reference, because Maestro evaluates <c>${...}</c> as JavaScript.
/// </summary>
public static partial class FlowGuard
{
    public const int MaxFlowBytes = 256 * 1024;

    /// <summary>Commands never allowed in flows MapCue generates or replays.</summary>
    public static IReadOnlySet<string> ForbiddenCommands { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "runScript",
        "evalScript",
        "assertTrue",
        "runFlow",
        "openLink",
        "setAirplaneMode",
        "toggleAirplaneMode",
        "clearKeychain",
        "addMedia",
        "travel",
        "setLocation",
    };

    private static readonly HashSet<string> _allowedHeaderKeys = new(StringComparer.Ordinal) { "appId", "name", "tags", "env" };

    public static IReadOnlyList<string> Check(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var problems = new List<string>();
        if (System.Text.Encoding.UTF8.GetByteCount(yaml) > MaxFlowBytes)
        {
            return [$"flow exceeds {MaxFlowBytes} bytes"];
        }

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            return [$"flow is not valid YAML (line {ex.Start.Line}, column {ex.Start.Column})"];
        }

        var documents = stream.Documents;
        if (documents.Count is 0 or > 2)
        {
            return ["flow must have an optional config document and one command document"];
        }

        if (documents.Count == 2)
        {
            if (documents[0].RootNode is not YamlMappingNode header)
            {
                problems.Add("flow config must be a mapping");
            }
            else
            {
                foreach (var (key, value) in header.Children)
                {
                    var name = (key as YamlScalarNode)?.Value ?? string.Empty;
                    if (!_allowedHeaderKeys.Contains(name))
                    {
                        problems.Add($"flow config key '{Shorten(name)}' is not allowed");
                    }

                    CheckScalars(value, problems);
                }
            }
        }

        var commands = documents[^1].RootNode;
        if (commands is not YamlSequenceNode)
        {
            problems.Add("flow commands must be a sequence");
        }

        Walk(commands, problems);
        return problems;
    }

    // Command names appear as sequence items (`- back`) or mapping keys (`- tapOn: ...`, nested blocks).
    // Mapping values that are scalars are arguments (selector text, ids) and are only checked for expressions.
    private static void Walk(YamlNode node, List<string> problems, bool commandPosition = true)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                if (commandPosition)
                {
                    CheckCommandName(scalar.Value, problems);
                }

                CheckScalar(scalar.Value, problems);
                break;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    Walk(child, problems);
                }

                break;
            case YamlMappingNode mapping:
                foreach (var (key, value) in mapping.Children)
                {
                    if (key is YamlScalarNode k)
                    {
                        CheckCommandName(k.Value, problems);
                        CheckScalar(k.Value, problems);
                    }

                    Walk(value, problems, commandPosition: false);
                }

                break;
        }
    }

    /// <summary>
    /// Returns the flow's <c>appId</c>: a literal, or a <c>${NAME}</c> reference resolved from the flow's own
    /// <c>env</c> block. Null when absent or not resolvable, so callers can refuse flows for unknown apps.
    /// </summary>
    public static string? ResolveAppId(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException)
        {
            return null;
        }

        if (stream.Documents.Count != 2 || stream.Documents[0].RootNode is not YamlMappingNode header)
        {
            return null;
        }

        var appId = Scalar(header, "appId");
        if (appId is null || !appId.Contains("${", StringComparison.Ordinal))
        {
            return appId;
        }

        var reference = WholeEnvReference().Match(appId);
        if (!reference.Success || !header.Children.TryGetValue(new YamlScalarNode("env"), out var env) || env is not YamlMappingNode envMap)
        {
            return null;
        }

        var resolved = Scalar(envMap, reference.Groups[1].Value);
        return resolved is not null && !resolved.Contains("${", StringComparison.Ordinal) ? resolved : null;
    }

    private static string? Scalar(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar ? scalar.Value : null;

    private static void CheckScalars(YamlNode node, List<string> problems)
    {
        foreach (var scalar in node.AllNodes.OfType<YamlScalarNode>())
        {
            CheckScalar(scalar.Value, problems);
        }
    }

    private static void CheckCommandName(string? value, List<string> problems)
    {
        if (value is not null && ForbiddenCommands.Contains(value))
        {
            problems.Add($"command '{value}' is forbidden");
        }
    }

    private static void CheckScalar(string? value, List<string> problems)
    {
        if (value is null || !value.Contains("${", StringComparison.Ordinal))
        {
            return;
        }

        var stripped = EnvReference().Replace(value, string.Empty);
        if (stripped.Contains("${", StringComparison.Ordinal))
        {
            problems.Add("JavaScript expressions (${...}) are forbidden; only ${ENV_NAME} references are allowed");
        }
    }

    private static string Shorten(string value) => value.Length <= 64 ? value : value[..64] + "...";

    [GeneratedRegex(@"\$\{[A-Z_][A-Z0-9_]{0,63}\}", RegexOptions.CultureInvariant)]
    private static partial Regex EnvReference();

    [GeneratedRegex(@"^\$\{([A-Z_][A-Z0-9_]{0,63})\}$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeEnvReference();
}
