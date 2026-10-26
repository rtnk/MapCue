using MapCue.Domain.Graph;

namespace MapCue.Application.Exploration;

/// <summary>Whether a snapshot shows the explored app (architecture §10.1: every action checks the app is in the foreground).</summary>
public enum AppPresence
{
    /// <summary>An element of the app's package is on screen.</summary>
    InApp,

    /// <summary>Another app's elements and none of the explored app (e.g. the launcher after <c>back</c> on a root screen).</summary>
    Left,

    /// <summary>
    /// Nothing tells: only framework ids (<c>android:id/*</c>, e.g. an <c>AlertDialog</c> window) or no resource ids at all.
    /// Treated as in the app; TapCue 0.1.0 does not report the foreground app (docs/tapcue-compatibility.md §9.3).
    /// </summary>
    Unknown,
}

public static class AppPresenceDetector
{
    /// <summary>Framework packages that do not identify an app (Android system dialogs use <c>android:id/*</c>).</summary>
    public static IReadOnlySet<string> FrameworkPackages { get; } = new HashSet<string>(StringComparer.Ordinal) { "android" };

    public static AppPresence Of(NormalizedSnapshot snapshot, string appId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (snapshot.ObservedPackages.Contains(appId, StringComparer.Ordinal))
        {
            return AppPresence.InApp;
        }

        return snapshot.ObservedPackages.Any(p => !FrameworkPackages.Contains(p)) ? AppPresence.Left : AppPresence.Unknown;
    }
}
