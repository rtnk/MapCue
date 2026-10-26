using System.Text.Json;
using MapCue.Application.Ports;

namespace MapCue.Adapters.Reasoning.Cli;

/// <summary>
/// Reads the <c>claude -p --output-format json</c> envelope [PoC STEP-04]: the answer is <c>structured_output</c> (with
/// <c>--json-schema</c>) or else the <c>result</c> text, taken verbatim (no fence stripping: a wrapped answer fails the schema and
/// gets the single repair). Usage: <c>usage.*_tokens</c> and <c>total_cost_usd</c>.
/// </summary>
public static class ClaudeEnvelope
{
    public sealed record Parsed(string? Answer, ReasonerUsage Usage, bool IsError, string? Problem);

    public static Parsed Parse(string stdout)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout.Trim());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new(null, ReasonerUsage.None with { Calls = 1 }, false, "the CLI output is not a JSON object");
            }

            var usage = UsageOf(root);
            var isError = root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True;
            if (isError)
            {
                var subtype = root.TryGetProperty("subtype", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                return new(null, usage, true, "the CLI reported an error" + (subtype is null ? string.Empty : $" ({Clip(subtype)})"));
            }

            if (root.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object)
            {
                return new(structured.GetRawText(), usage, false, null);
            }

            return root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
                ? new(result.GetString(), usage, false, null)
                : new(null, usage, false, "the CLI output has no result");
        }
        catch (JsonException)
        {
            return new(null, ReasonerUsage.None with { Calls = 1 }, false, "the CLI output is not JSON");
        }
    }

    private static ReasonerUsage UsageOf(JsonElement root)
    {
        long? input = null;
        long? output = null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens" })
            {
                if (usage.TryGetProperty(name, out var v) && v.TryGetInt64(out var n))
                {
                    input = (input ?? 0) + n;
                }
            }

            if (usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var on))
            {
                output = on;
            }
        }

        decimal? cost = root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDecimal(out var d) ? d : null;
        return new ReasonerUsage(1, input, output, cost);
    }

    private static string Clip(string value) =>
        new([.. value.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-').Take(40)]);
}
