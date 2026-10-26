using System.Collections.ObjectModel;

namespace MapCue.DemoApp;

/// <summary>Test profiles of the demo app. There is no backend and no real data: everything is fixed and in memory.</summary>
public enum DemoProfile
{
    Basic,
    Investor,
}

public sealed record DemoAccount(string Name, string MaskedNumber, string Balance)
{
    /// <summary>Row text in the accounts list.</summary>
    public string RowText => $"{Name} · {Balance}";
}

public sealed record DemoHolding(string Name, string Share)
{
    public string RowText => $"{Name} · {Share}";
}

/// <summary>
/// State of one app session: the chosen profile and scenario. Deleting an account changes only this in-memory list,
/// so every launch starts from the same data.
/// </summary>
public sealed class DemoSession
{
    private DemoSession(DemoProfile profile, bool emptyAccounts)
    {
        Profile = profile;
        EmptyAccounts = emptyAccounts;
        Accounts = new ObservableCollection<DemoAccount>(emptyAccounts ? [] : DemoData.AccountsFor(profile));
    }

    public DemoProfile Profile { get; }

    public bool EmptyAccounts { get; }

    public bool HasInvestments => Profile == DemoProfile.Investor;

    public ObservableCollection<DemoAccount> Accounts { get; }

    public static DemoSession Start(DemoProfile profile, bool emptyAccounts) => new(profile, emptyAccounts);
}

public static class DemoData
{
    /// <summary>Deterministic duration of the loading screen after a profile is chosen.</summary>
    public static readonly TimeSpan LoadingDelay = TimeSpan.FromMilliseconds(1500);

    public static IReadOnlyList<DemoAccount> AccountsFor(DemoProfile profile)
    {
        List<DemoAccount> accounts =
        [
            new("Everyday account", "•••• 0001", "PLN 1,250.00"),
            new("Savings account", "•••• 0002", "PLN 8,400.00"),
        ];
        if (profile == DemoProfile.Investor)
        {
            accounts.Add(new("Brokerage account", "•••• 0003", "PLN 52,310.75"));
        }

        return accounts;
    }

    public static IReadOnlyList<DemoHolding> Holdings { get; } =
    [
        new("Global equity fund", "41%"),
        new("Bond fund", "35%"),
        new("Cash", "24%"),
    ];
}
