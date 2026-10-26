using System.Text.RegularExpressions;
using MapCue.Application.Agent;
using MapCue.Domain;

namespace MapCue.Application.Tests.Agent;

public class RiskClassifierTests
{
    private static readonly RiskClassifier _rules = new();

    private static RiskClass Tap(string? label, string? resourceId = null, string role = "button") =>
        _rules.Classify(ActionType.Tap, role, resourceId, [label]).Risk;

    [Theory]
    [InlineData("Accounts", RiskClass.Navigational)]
    [InlineData("Investments", RiskClass.Navigational)]
    [InlineData("Next page", RiskClass.Navigational)]
    [InlineData("Settings", RiskClass.Navigational)]
    [InlineData("Delete", RiskClass.Destructive)]
    [InlineData("Usuń", RiskClass.Destructive)]
    [InlineData("usuń wiadomość", RiskClass.Destructive)]
    [InlineData("Löschen", RiskClass.Destructive)]
    [InlineData("Supprimer", RiskClass.Destructive)]
    [InlineData("Eliminar", RiskClass.Destructive)]
    [InlineData("Pay now", RiskClass.Financial)]
    [InlineData("Zapłać", RiskClass.Financial)]
    [InlineData("Wykonaj przelew", RiskClass.Financial)]
    [InlineData("Jetzt kaufen", RiskClass.Financial)]
    [InlineData("Pagar", RiskClass.Financial)]
    [InlineData("Wyślij", RiskClass.Communication)]
    [InlineData("Share", RiskClass.Communication)]
    [InlineData("Condividi", RiskClass.Communication)]
    [InlineData("Wyloguj", RiskClass.AccountChange)]
    [InlineData("Sign out", RiskClass.AccountChange)]
    [InlineData("Zmień hasło", RiskClass.AccountChange)]
    [InlineData("Delete account", RiskClass.AccountChange)]
    [InlineData("Rate this app", RiskClass.External)]
    [InlineData("Otwórz w przeglądarce", RiskClass.External)]
    [InlineData("Save", RiskClass.StateChanging)]
    [InlineData("Zapisz", RiskClass.StateChanging)]
    [InlineData("Dodaj konto", RiskClass.StateChanging)]
    [InlineData("Bestätigen", RiskClass.StateChanging)]
    public void Keywords_in_several_languages_raise_the_risk(string label, RiskClass expected)
    {
        Assert.Equal(expected, Tap(label));
    }

    [Theory]
    [InlineData("app:id/btnDeleteAccount", RiskClass.AccountChange)]
    [InlineData("com.bank:id/pay_button", RiskClass.Financial)]
    [InlineData("app:id/tab_accounts", RiskClass.Navigational)]
    public void Resource_ids_are_split_into_words(string resourceId, RiskClass expected)
    {
        Assert.Equal(expected, Tap(null, resourceId));
    }

    [Fact]
    public void Base_risk_depends_on_the_action_and_keywords_only_apply_to_actions_on_the_element()
    {
        Assert.Equal(RiskClass.Safe, _rules.Classify(ActionType.Back, null).Risk);
        Assert.Equal(RiskClass.Safe, _rules.Classify(ActionType.Scroll, "list", "app:id/delete_list", ["Delete"]).Risk);
        Assert.Equal(RiskClass.StateChanging, _rules.Classify(ActionType.InputText, "textField", "app:id/name", [null]).Risk);
        Assert.Equal(RiskClass.Unknown, _rules.Classify(ActionType.LongPress, "button", null, ["Open"]).Risk);
        Assert.Equal(RiskClass.StateChanging, Tap("Dark mode", "app:id/dark_mode", "toggle"));
    }

    [Fact]
    public void Operator_rules_only_raise_the_risk_and_fail_closed()
    {
        var rules = new RiskClassifier(
        [
            new OperatorRiskRule("blik", RiskClass.Financial, ResourceId: "app:id/blik"),
            new OperatorRiskRule("admin", RiskClass.Destructive, ResourceIdPattern: new Regex("^app:id/admin_", RegexOptions.None, TimeSpan.FromMilliseconds(100))),
            new OperatorRiskRule("lower", RiskClass.Navigational, LabelPattern: new Regex("Delete", RegexOptions.None, TimeSpan.FromMilliseconds(100))),
        ]);

        var blik = rules.Classify(ActionType.Tap, "button", "app:id/blik", ["BLIK"]);
        Assert.Equal(RiskClass.Financial, blik.Risk);
        Assert.Contains("operator:blik", blik.Reasons);
        Assert.Equal(RiskClass.Destructive, rules.Classify(ActionType.Tap, "button", "app:id/admin_reset_cache", ["Tools"]).Risk);
        Assert.Equal(RiskClass.Destructive, rules.Classify(ActionType.Tap, "button", null, ["Delete"]).Risk);
    }

    [Fact]
    public void Max_is_the_riskier_class()
    {
        Assert.Equal(RiskClass.Destructive, RiskClassifier.Max(RiskClass.Navigational, RiskClass.Destructive));
        Assert.Equal(RiskClass.Unknown, RiskClassifier.Max(RiskClass.Unknown, RiskClass.StateChanging));
        Assert.Equal(RiskClass.External, RiskClassifier.Max(RiskClass.External, RiskClass.Safe));
    }

    [Fact]
    public void Folding_handles_diacritics_without_icu()
    {
        Assert.Equal(["zaplac", "teraz"], TextFold.Words("ZAPŁAĆ teraz!"));
        Assert.Equal(["losch", "uberweisung"], TextFold.Words("Lösch Überweisung"));
    }
}
