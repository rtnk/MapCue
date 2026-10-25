using MapCue.Application.Graph;

namespace MapCue.Application.Tests.Graph;

public class TextMaskerTests
{
    [Theory]
    [InlineData("Balance: 1 234,56 zł", "balance: ‹amount›")]
    [InlineData("$12.99 per month", "‹amount› per month")]
    [InlineData("Paid 45.00 EUR", "paid ‹amount›")]
    [InlineData("Sun, Oct 25", "‹date›")]
    [InlineData("Due 25 października 2026", "due ‹date›")]
    [InlineData("2026-10-25T22:18:00Z", "‹date›")]
    [InlineData("25.10.2026", "‹date›")]
    [InlineData("Updated 10:18 PM", "updated ‹time›")]
    [InlineData("anna@example.com wrote", "‹email› wrote")]
    [InlineData("IBAN PL61 1090 1014 0000 0712 1981 2874", "iban ‹iban›")]
    [InlineData("3 new messages", "# new messages")]
    [InlineData("Invoice 2024/117", "invoice #/#")]
    [InlineData("  Multiple   SPACES  ", "multiple spaces")]
    [InlineData("Contact [REDACTED:phone]", "contact ‹redacted›")]
    public void Masks_dynamic_content(string text, string expected) => Assert.Equal(expected, TextMasker.Mask(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_text_has_no_label(string? text) => Assert.Null(TextMasker.Mask(text));

    [Fact]
    public void Numbers_of_any_length_mask_the_same()
    {
        Assert.Equal(TextMasker.Mask("7 items"), TextMasker.Mask("1024 items"));
        Assert.Equal(TextMasker.Mask("Order 12,345.67"), TextMasker.Mask("Order 3"));
    }

    [Fact]
    public void Input_values_are_masked_longest_first()
    {
        Assert.Equal("hi ‹input› (‹input›)", TextMasker.Mask("Hi anna.k (anna)", ["anna", "anna.k"]));
    }
}
