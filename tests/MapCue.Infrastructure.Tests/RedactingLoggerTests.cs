using System.Text.Json;
using MapCue.Domain.Secrets;
using MapCue.Infrastructure.Artifacts.Logging;
using MapCue.Infrastructure.Artifacts.Redaction;
using MapCue.Infrastructure.Artifacts.Secrets;
using MapCue.Testing;
using Microsoft.Extensions.Logging;

namespace MapCue.Infrastructure.Tests;

public class RedactingLoggerTests
{
    private const string SecretValue = "Zx9-very-secret-Pa55";

    private static (StringWriter Output, ILogger Logger, EnvironmentSecretResolver Resolver) Create(LogLevel level = LogLevel.Trace)
    {
        var output = new StringWriter();
        var redactor = new PatternRedactor();
        var provider = new JsonLinesLoggerProvider(output, redactor, new ManualClock(), level);
        var resolver = new EnvironmentSecretResolver(redactor, name => name == "MAPCUE_TEST_SECRET" ? SecretValue : null);
        return (output, provider.CreateLogger("Test"), resolver);
    }

    [Fact]
    public void Resolved_secret_never_reaches_the_log()
    {
        var (output, logger, resolver) = Create();
        var secret = resolver.Resolve(SecretRef.Parse("secret://env/MAPCUE_TEST_SECRET"));

        // Every channel a careless caller could use: template argument, interpolated message, exception.
        logger.LogInformation("Logging in with {Password}", secret.Reveal());
#pragma warning disable CA2254 // Deliberate misuse: interpolated template must still be redacted.
        logger.LogWarning($"Raw interpolation {secret.Reveal()}");
#pragma warning restore CA2254
        logger.LogError(new InvalidOperationException($"failed for {secret.Reveal()}"), "Login failed");
        logger.LogInformation("Wrapper renders as {Secret}", secret);

        var log = output.ToString();
        Assert.DoesNotContain(SecretValue, log, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:secret]", log, StringComparison.Ordinal);
        Assert.Contains("\"Secret\":\"***\"", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Pattern_secrets_are_redacted_even_when_not_registered()
    {
        var (output, logger, _) = Create();
        logger.LogInformation("Header {Header}", "Bearer abc.def.ghi-123");
        Assert.DoesNotContain("abc.def.ghi-123", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Each_entry_is_one_structured_json_line()
    {
        var (output, logger, _) = Create();
        logger.LogInformation(new EventId(7, "RunStarted"), "Run {RunId} started with {Budget} actions", "run_0001", 300);

        var line = Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        Assert.Equal("2026-01-01T00:00:00.0000000+00:00", root.GetProperty("ts").GetString());
        Assert.Equal("Information", root.GetProperty("level").GetString());
        Assert.Equal("Test", root.GetProperty("category").GetString());
        Assert.Equal(7, root.GetProperty("eventId").GetInt32());
        Assert.Equal("Run run_0001 started with 300 actions", root.GetProperty("message").GetString());
        Assert.Equal("run_0001", root.GetProperty("props").GetProperty("RunId").GetString());
        Assert.Equal(300, root.GetProperty("props").GetProperty("Budget").GetInt32());
        Assert.Equal("Run {RunId} started with {Budget} actions", root.GetProperty("props").GetProperty("template").GetString());
    }

    [Fact]
    public void Entries_below_the_minimum_level_are_dropped()
    {
        var (output, logger, _) = Create(LogLevel.Warning);
        logger.LogInformation("hidden");
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void Missing_secret_error_names_only_the_reference()
    {
        var (_, _, resolver) = Create();
        var ex = Assert.Throws<SecretUnavailableException>(() => resolver.Resolve(SecretRef.Parse("secret://env/NOT_SET")));
        Assert.Equal("Secret secret://env/NOT_SET is not set.", ex.Message);
    }
}
