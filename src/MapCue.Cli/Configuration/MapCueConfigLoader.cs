using System.Text.RegularExpressions;
using MapCue.Application.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MapCue.Cli.Configuration;

/// <summary>
/// Loads <c>mapcue.yaml</c> strictly: unknown keys and duplicate keys are errors, the file size is capped,
/// and parser messages are reduced to location and key name so that values never reach the console or logs.
/// </summary>
public static partial class MapCueConfigLoader
{
    public const long MaxFileBytes = 1024 * 1024;

    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .Build();

    public static ConfigValidationResult LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string text;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return ConfigValidationResult.Failure(new ConfigError(ConfigErrorCodes.FileUnreadable, "$", "Configuration file not found."));
            }

            if (info.Length > MaxFileBytes)
            {
                return ConfigValidationResult.Failure(new ConfigError(ConfigErrorCodes.FileUnreadable, "$", $"Configuration file exceeds {MaxFileBytes} bytes."));
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConfigValidationResult.Failure(new ConfigError(ConfigErrorCodes.FileUnreadable, "$", "Configuration file cannot be read."));
        }

        return LoadText(text);
    }

    public static ConfigValidationResult LoadText(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        MapCueConfigDocument? document;
        try
        {
            document = _deserializer.Deserialize<MapCueConfigDocument?>(yaml);
        }
        catch (YamlException ex)
        {
            return ConfigValidationResult.Failure(new ConfigError(ConfigErrorCodes.SyntaxError, $"line {ex.Start.Line}, column {ex.Start.Column}", Describe(ex)));
        }

        return MapCueConfigValidator.Validate(document);
    }

    // YamlDotNet messages (and inner exceptions) may quote the offending value; only key names are kept.
    private static string Describe(YamlException ex)
    {
        var unknown = UnknownPropertyPattern().Match(ex.Message);
        if (unknown.Success)
        {
            return $"Unknown key '{unknown.Groups[1].Value}'.";
        }

        if (ex.Message.Contains("Duplicate key", StringComparison.OrdinalIgnoreCase))
        {
            return "Duplicate key.";
        }

        return ex.InnerException is null ? "Invalid YAML syntax." : "Value has an invalid type or format.";
    }

    [GeneratedRegex("Property '([A-Za-z0-9_-]{1,64})' not found", RegexOptions.CultureInvariant)]
    private static partial Regex UnknownPropertyPattern();
}
