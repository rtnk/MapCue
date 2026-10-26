using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MapCue.Contracts.Schemas;

/// <summary>A schema violation. <see cref="InstancePath"/> is a JSON Pointer whose unusual segments are replaced by <c>?</c>.</summary>
public sealed record SchemaError(string InstancePath, string Keyword, string Message)
{
    public override string ToString() => $"{(InstancePath.Length == 0 ? "/" : InstancePath)}: {Message}";
}

/// <summary>
/// A JSON Schema 2020-12 validator for the subset of keywords MapCue's own contracts use. It fails closed: a schema with any
/// other keyword is refused when loaded, so a schema change can never be silently ignored. Instances with duplicate property
/// names are invalid (another parser could pick the other value). Tests compare its verdicts with a full implementation.
/// </summary>
public sealed class JsonSchema
{
    private static readonly HashSet<string> _supported = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$defs", "$ref", "title", "description",
        "type", "properties", "required", "additionalProperties",
        "items", "minItems", "maxItems", "uniqueItems",
        "enum", "const", "pattern", "minLength", "maxLength", "minimum", "maximum",
        "anyOf", "oneOf",
    };

    private static readonly TimeSpan _patternTimeout = TimeSpan.FromMilliseconds(250);

    private readonly JsonElement _root;
    private readonly Dictionary<string, Regex> _patterns = new(StringComparer.Ordinal);

    private JsonSchema(JsonElement root)
    {
        _root = root;
        Check(root, "#");
    }

    /// <summary>Loads a schema. Throws <see cref="NotSupportedException"/> for keywords outside the supported subset.</summary>
    public static JsonSchema Parse(string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        return new JsonSchema(document.RootElement.Clone());
    }

    /// <summary>Validates <paramref name="instance"/>; returns at most <paramref name="maxErrors"/> errors (empty when valid).</summary>
    public IReadOnlyList<SchemaError> Validate(JsonElement instance, int maxErrors = 20)
    {
        var errors = new List<SchemaError>();
        Validate(_root, instance, string.Empty, errors, maxErrors);
        return errors;
    }

    private void Check(JsonElement schema, string location)
    {
        if (schema.ValueKind == JsonValueKind.True || schema.ValueKind == JsonValueKind.False)
        {
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new NotSupportedException($"Schema at {location} must be an object or a boolean.");
        }

        foreach (var keyword in schema.EnumerateObject())
        {
            if (!_supported.Contains(keyword.Name))
            {
                throw new NotSupportedException($"Schema keyword '{keyword.Name}' at {location} is not supported by MapCue's validator.");
            }

            switch (keyword.Name)
            {
                case "$defs" or "properties":
                    foreach (var child in keyword.Value.EnumerateObject())
                    {
                        Check(child.Value, $"{location}/{keyword.Name}/{child.Name}");
                    }

                    break;
                case "items" or "additionalProperties":
                    Check(keyword.Value, $"{location}/{keyword.Name}");
                    break;
                case "anyOf" or "oneOf":
                    var i = 0;
                    foreach (var child in keyword.Value.EnumerateArray())
                    {
                        Check(child, $"{location}/{keyword.Name}/{i++}");
                    }

                    break;
                case "$ref":
                    Resolve(keyword.Value.GetString()!);
                    break;
                case "pattern":
                    PatternOf(keyword.Value.GetString()!);
                    break;
            }
        }
    }

    private JsonElement Resolve(string reference)
    {
        const string prefix = "#/$defs/";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal)
            || !_root.TryGetProperty("$defs", out var defs)
            || !defs.TryGetProperty(reference[prefix.Length..], out var target))
        {
            throw new NotSupportedException($"Only local '#/$defs/<name>' references are supported: '{reference}'.");
        }

        return target;
    }

    private Regex PatternOf(string pattern)
    {
        if (!_patterns.TryGetValue(pattern, out var regex))
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, _patternTimeout);
            _patterns[pattern] = regex;
        }

        return regex;
    }

    private bool IsValid(JsonElement schema, JsonElement instance, string path)
    {
        var errors = new List<SchemaError>();
        Validate(schema, instance, path, errors, 1);
        return errors.Count == 0;
    }

    private void Validate(JsonElement schema, JsonElement instance, string path, List<SchemaError> errors, int maxErrors)
    {
        if (errors.Count >= maxErrors || schema.ValueKind == JsonValueKind.True)
        {
            return;
        }

        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add(new(path, "false", "is not allowed"));
            return;
        }

        void Fail(string keyword, string message)
        {
            if (errors.Count < maxErrors)
            {
                errors.Add(new(path, keyword, message));
            }
        }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            Validate(Resolve(reference.GetString()!), instance, path, errors, maxErrors);
        }

        if (schema.TryGetProperty("type", out var type) && !MatchesType(type, instance))
        {
            Fail("type", $"must be of type {type.GetRawText()}, not {Describe(instance)}");
            return;
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, instance))
        {
            Fail("const", $"must be {constant.GetRawText()}");
        }

        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(a => JsonElement.DeepEquals(a, instance)))
        {
            Fail("enum", "is not one of the allowed values");
        }

        if (schema.TryGetProperty("anyOf", out var anyOf) && !anyOf.EnumerateArray().Any(s => IsValid(s, instance, path)))
        {
            Fail("anyOf", "does not match any allowed shape");
        }

        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.EnumerateArray().Count(s => IsValid(s, instance, path)) != 1)
        {
            Fail("oneOf", "must match exactly one allowed shape");
        }

        switch (instance.ValueKind)
        {
            case JsonValueKind.String:
                ValidateString(schema, instance.GetString()!, Fail);
                break;
            case JsonValueKind.Number:
                ValidateNumber(schema, instance, Fail);
                break;
            case JsonValueKind.Array:
                ValidateArray(schema, instance, path, errors, maxErrors, Fail);
                break;
            case JsonValueKind.Object:
                ValidateObject(schema, instance, path, errors, maxErrors, Fail);
                break;
        }
    }

    private void ValidateString(JsonElement schema, string value, Action<string, string> fail)
    {
        var length = value.EnumerateRunes().Count();
        if (schema.TryGetProperty("minLength", out var min) && length < min.GetInt32())
        {
            fail("minLength", $"must have at least {min.GetInt32()} characters");
        }

        if (schema.TryGetProperty("maxLength", out var max) && length > max.GetInt32())
        {
            fail("maxLength", $"must have at most {max.GetInt32()} characters");
        }

        if (schema.TryGetProperty("pattern", out var pattern))
        {
            bool matches;
            try
            {
                matches = PatternOf(pattern.GetString()!).IsMatch(value);
            }
            catch (RegexMatchTimeoutException)
            {
                matches = false;
            }

            if (!matches)
            {
                fail("pattern", "has an invalid format");
            }
        }
    }

    private static void ValidateNumber(JsonElement schema, JsonElement instance, Action<string, string> fail)
    {
        var value = instance.TryGetDecimal(out var d) ? (double)d : instance.GetDouble();
        if (schema.TryGetProperty("minimum", out var min) && value < min.GetDouble())
        {
            fail("minimum", $"must be ≥ {min.GetRawText()}");
        }

        if (schema.TryGetProperty("maximum", out var max) && value > max.GetDouble())
        {
            fail("maximum", $"must be ≤ {max.GetRawText()}");
        }
    }

    private void ValidateArray(JsonElement schema, JsonElement instance, string path, List<SchemaError> errors, int maxErrors, Action<string, string> fail)
    {
        var count = instance.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var min) && count < min.GetInt32())
        {
            fail("minItems", $"must have at least {min.GetInt32()} items");
        }

        if (schema.TryGetProperty("maxItems", out var max) && count > max.GetInt32())
        {
            fail("maxItems", $"must have at most {max.GetInt32()} items");
            return;
        }

        if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean())
        {
            var items = instance.EnumerateArray().ToList();
            for (var i = 0; i < items.Count; i++)
            {
                if (items.Take(i).Any(previous => JsonElement.DeepEquals(previous, items[i])))
                {
                    fail("uniqueItems", "must not contain duplicates");
                    break;
                }
            }
        }

        if (schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in instance.EnumerateArray())
            {
                Validate(itemSchema, item, path + "/" + index.ToString(CultureInfo.InvariantCulture), errors, maxErrors);
                index++;
            }
        }
    }

    private void ValidateObject(JsonElement schema, JsonElement instance, string path, List<SchemaError> errors, int maxErrors, Action<string, string> fail)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in instance.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                fail("duplicate", $"has a duplicate property {Segment(property.Name)}");
                return;
            }
        }

        schema.TryGetProperty("properties", out var properties);
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()!).Where(n => !seen.Contains(n)))
            {
                fail("required", $"is missing required property '{name}'");
            }
        }

        foreach (var property in instance.EnumerateObject())
        {
            var childPath = path + "/" + Segment(property.Name);
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                Validate(propertySchema, property.Value, childPath, errors, maxErrors);
            }
            else if (schema.TryGetProperty("additionalProperties", out var additional))
            {
                if (additional.ValueKind == JsonValueKind.False)
                {
                    fail("additionalProperties", $"has a property that is not allowed: {Segment(property.Name)}");
                }
                else
                {
                    Validate(additional, property.Value, childPath, errors, maxErrors);
                }
            }
        }
    }

    private static bool MatchesType(JsonElement type, JsonElement instance) =>
        type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(t => MatchesType(t.GetString()!, instance))
            : MatchesType(type.GetString()!, instance);

    private static bool MatchesType(string type, JsonElement instance) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        "number" => instance.ValueKind == JsonValueKind.Number,
        "integer" => instance.ValueKind == JsonValueKind.Number && IsInteger(instance),
        _ => throw new NotSupportedException($"Unknown schema type '{type}'."),
    };

    private static bool IsInteger(JsonElement instance) =>
        instance.TryGetDecimal(out var d) ? decimal.Truncate(d) == d : Math.Floor(instance.GetDouble()) == instance.GetDouble();

    private static string Describe(JsonElement instance) => instance.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Undefined => "undefined",
        _ => instance.ValueKind.ToString().ToLowerInvariant(),
    };

    /// <summary>Property names come from untrusted input: only plain identifiers are echoed (errors may be shown to the reasoner on repair).</summary>
    private static string Segment(string name)
    {
        if (name.Length is 0 or > 64)
        {
            return "?";
        }

        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return "?";
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
