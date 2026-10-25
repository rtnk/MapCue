using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MapCue.Application.Ports;
using MapCue.Domain;

namespace MapCue.Adapters.TapCue.Flows;

/// <summary>
/// A generated single-action flow. <see cref="Yaml"/> never contains input values: they travel separately in
/// <see cref="Environment"/> (passed to TapCue as <c>env</c>) and must never be persisted.
/// <see cref="ShapeKey"/> identifies the template and the set of optional fields, so a syntax check of one
/// instance covers every flow with the same shape.
/// </summary>
public sealed record GeneratedFlow(string Yaml, string ShapeKey, IReadOnlyDictionary<string, string> Environment);

/// <summary>Why an action could not be turned into a flow. The engine treats it as <see cref="ActionOutcome.Rejected"/>.</summary>
public sealed class FlowGenerationException(string message) : Exception(message);

/// <summary>
/// Turns one <see cref="ValidatedDeviceAction"/> into a short flow from fixed templates (architecture §10.1 layer 4).
/// Values come only from typed fields, are regex-escaped and YAML-quoted, and the result is checked by
/// <see cref="FlowGuard"/>. Action types without a template are rejected instead of being approximated.
/// </summary>
public sealed partial class FlowYamlGenerator
{
    public const int MaxSelectorLength = 512;
    public const string InputEnvironmentName = "MAPCUE_IN_1";

    private readonly HashSet<string> _allowedAppIds;
    private readonly TimeSpan _settleTimeout;

    public FlowYamlGenerator(IEnumerable<string> allowedAppIds, TimeSpan settleTimeout)
    {
        ArgumentNullException.ThrowIfNull(allowedAppIds);
        _allowedAppIds = allowedAppIds.ToHashSet(StringComparer.Ordinal);
        _settleTimeout = settleTimeout;
    }

    /// <param name="action">The validated action; its <see cref="ValidatedDeviceAction.AppId"/> must be allowlisted.</param>
    /// <param name="resolveInput">Resolves <see cref="ValidatedDeviceAction.InputValueRef"/> to its value at execution time; null if unavailable.</param>
    public GeneratedFlow Generate(ValidatedDeviceAction action, Func<string, string?>? resolveInput = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!ActionIdPattern().IsMatch(action.ActionId))
        {
            throw new FlowGenerationException("actionId must match [A-Za-z0-9._-]{1,128}.");
        }

        if (!_allowedAppIds.Contains(action.AppId) || !AppIdPattern().IsMatch(action.AppId))
        {
            throw new FlowGenerationException("appId is not in the allowlist.");
        }

        var env = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var body = new StringBuilder();
        string shape;
        var settle = true;
        switch (action.Type)
        {
            case ActionType.Tap:
            case ActionType.LongPress:
                var command = action.Type == ActionType.Tap ? "tapOn" : "longPressOn";
                shape = command + Selector(action, body, command);
                if (action.Type == ActionType.Tap)
                {
                    body.Append("    retryTapIfNoChange: false\n");
                }

                break;
            case ActionType.Back:
                shape = "back";
                body.Append("- back\n");
                break;
            case ActionType.HideKeyboard:
                shape = "hideKeyboard";
                body.Append("- hideKeyboard\n");
                break;
            case ActionType.LaunchApp:
                shape = "launchApp";
                body.Append("- launchApp\n");
                break;
            case ActionType.WaitForAnimationToEnd:
                // The settle step below is the whole flow.
                shape = "waitForAnimationToEnd";
                break;
            case ActionType.Scroll:
                var scroll = action.Direction ?? ScrollDirection.Down;
                if (scroll == ScrollDirection.Down)
                {
                    shape = "scroll";
                    body.Append("- scroll\n");
                }
                else
                {
                    // Revealing content above/left/right means moving the finger the opposite way.
                    shape = "swipe";
                    body.Append("- swipe:\n    direction: ").Append(SwipeName(Opposite(scroll))).Append('\n');
                }

                break;
            case ActionType.Swipe:
                shape = "swipe";
                body.Append("- swipe:\n    direction: ").Append(SwipeName(action.Direction ?? throw new FlowGenerationException("swipe needs a direction."))).Append('\n');
                break;
            case ActionType.InputText:
                if (string.IsNullOrEmpty(action.InputValueRef))
                {
                    throw new FlowGenerationException("inputText needs an input reference; literal values are not allowed.");
                }

                var value = resolveInput?.Invoke(action.InputValueRef)
                    ?? throw new FlowGenerationException("the input reference cannot be resolved.");
                env[InputEnvironmentName] = value;
                shape = "inputText";
                body.Append("- inputText: \"${").Append(InputEnvironmentName).Append("}\"\n");
                settle = false;
                break;
            default:
                throw new FlowGenerationException($"action type {action.Type} has no flow template in this adapter.");
        }

        if (settle)
        {
            body.Append("- waitForAnimationToEnd:\n    timeout: ")
                .Append(((long)_settleTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        var yaml = new StringBuilder()
            .Append("appId: ").Append(MaestroText.YamlQuote(action.AppId)).Append('\n')
            .Append("name: ").Append(MaestroText.YamlQuote("mapcue-" + action.ActionId)).Append('\n')
            .Append("---\n")
            .Append(body)
            .ToString();

        var problems = FlowGuard.Check(yaml);
        if (problems.Count > 0)
        {
            throw new FlowGenerationException("generated flow violates policy: " + string.Join("; ", problems));
        }

        return new GeneratedFlow(yaml, $"{shape}{(settle ? "+settle" : string.Empty)}", env);
    }

    private static string Selector(ValidatedDeviceAction action, StringBuilder body, string command)
    {
        var id = action.TargetResourceId;
        var text = action.TargetText;
        if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(text))
        {
            throw new FlowGenerationException($"{command} needs a resource id or a text selector.");
        }

        if ((id?.Length ?? 0) > MaxSelectorLength || (text?.Length ?? 0) > MaxSelectorLength)
        {
            throw new FlowGenerationException($"selector values are limited to {MaxSelectorLength} characters.");
        }

        body.Append("- ").Append(command).Append(":\n");
        var shape = new StringBuilder();
        if (!string.IsNullOrEmpty(id))
        {
            body.Append("    id: ").Append(MaestroText.YamlQuote(MaestroText.EscapeRegex(id))).Append('\n');
            shape.Append("[id]");
        }

        if (!string.IsNullOrEmpty(text))
        {
            body.Append("    text: ").Append(MaestroText.YamlQuote(MaestroText.EscapeRegex(text))).Append('\n');
            shape.Append("[text]");
        }

        return shape.ToString();
    }

    private static ScrollDirection Opposite(ScrollDirection direction) => direction switch
    {
        ScrollDirection.Up => ScrollDirection.Down,
        ScrollDirection.Down => ScrollDirection.Up,
        ScrollDirection.Left => ScrollDirection.Right,
        _ => ScrollDirection.Left,
    };

    private static string SwipeName(ScrollDirection direction) => direction switch
    {
        ScrollDirection.Up => "UP",
        ScrollDirection.Down => "DOWN",
        ScrollDirection.Left => "LEFT",
        _ => "RIGHT",
    };

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex ActionIdPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppIdPattern();
}
