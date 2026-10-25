using MapCue.Application.Ports;
using MapCue.Infrastructure.Artifacts.Redaction;

namespace MapCue.Infrastructure.Tests;

public class PatternRedactorTests
{
    private static string Redact(string text, PatternRedactor? redactor = null) =>
        (redactor ?? new PatternRedactor()).Redact(text, RedactionContext.Log).Text;

    [Fact]
    public void Registered_values_are_always_redacted_longest_first()
    {
        var redactor = new PatternRedactor();
        redactor.RegisterSensitiveValue("abc");
        redactor.RegisterSensitiveValue("abcdef");

        var result = redactor.Redact("x=abcdef y=abc", RedactionContext.Log);

        Assert.Equal("x=[REDACTED:secret] y=[REDACTED:secret]", result.Text);
        Assert.Equal(2, result.RedactionCount);
        Assert.Equal(["secret"], result.RulesApplied);
    }

    [Theory]
    [InlineData("Authorization: Bearer abc.DEF-123_xyz", "bearer")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl here", "jwt")]
    [InlineData("contact jan.kowalski@example.com now", "email")]
    [InlineData("iban PL61 1090 1014 0000 0712 1981 2874", "iban")]
    [InlineData("card 4111 1111 1111 1111 ok", "pan")]
    [InlineData("call +48 601 234 567", "phone")]
    [InlineData("password=hunter2", "credential")]
    [InlineData("{\"apiKey\": \"k-123\"}", "credential")]
    public void Sensitive_patterns_are_redacted(string input, string rule)
    {
        var result = new PatternRedactor().Redact(input, RedactionContext.Log);
        Assert.Contains($"[REDACTED:{rule}]", result.Text, StringComparison.Ordinal);
        Assert.Contains(rule, result.RulesApplied);
    }

    [Theory]
    [InlineData("hunter2", "password=hunter2")]
    [InlineData("k-123", "{\"apiKey\": \"k-123\"}")]
    public void Credential_values_are_removed(string secret, string input)
    {
        Assert.DoesNotContain(secret, Redact(input), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Run run_0001 finished in 1234567 ms")]
    [InlineData("Screen S-12 has 4 tabs; maxActions=300")]
    [InlineData("card-like 4111 1111 1111 1112 fails Luhn")]
    [InlineData("secret://env/MAPCUE_DEMO_PASSWORD is a reference, not a value")]
    public void Ordinary_text_is_left_alone(string input)
    {
        Assert.Equal(input, Redact(input));
    }

    [Fact]
    public void Empty_values_are_not_registered()
    {
        var redactor = new PatternRedactor();
        redactor.RegisterSensitiveValue(string.Empty);
        Assert.Equal("unchanged", Redact("unchanged", redactor));
    }
}
