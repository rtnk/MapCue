using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.Exploration;

/// <summary>A settled (or timed out) observation and its normalization.</summary>
public sealed record StableObservation(RawObservation Observation, NormalizedSnapshot Normalized, int Observations, bool Stable);

/// <summary>
/// UI stabilization after an action (architecture §5.1, §7.3 step 6): observe until two consecutive observations have the same
/// fingerprint (<c>identityHash</c>) and show no loading indicator, or until <c>uiSettleTimeout</c>. A screen still loading at the
/// timeout is returned as it is (a <c>loading</c> state, <see cref="StableObservation.Stable"/> false). The number of observations
/// is bounded by the timeout and the poll interval, so a clock that does not move cannot loop forever.
/// </summary>
public sealed class UiStabilizer(IDeviceAutomation device, IClock clock, TimeSpan settleTimeout, TimeSpan pollInterval, Func<TimeSpan, CancellationToken, Task> delay)
{
    public const string LoadingDiscriminator = "loading";

    public int MaxObservations => Math.Max(2, (int)Math.Ceiling(settleTimeout / (pollInterval > TimeSpan.Zero ? pollInterval : TimeSpan.FromMilliseconds(1))) + 1);

    public async Task<StableObservation> ObserveAsync(DeviceId deviceId, string appId, NormalizationOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var started = clock.UtcNow;
        var observation = await device.ObserveAsync(deviceId, new ObservationRequest(appId, ObservationFidelity.Fast), ct);
        var normalized = SnapshotNormalizer.Normalize(observation, options);
        var count = 1;
        while (count < MaxObservations && clock.UtcNow - started < settleTimeout)
        {
            await delay(pollInterval, ct);
            var next = await device.ObserveAsync(deviceId, new ObservationRequest(appId, ObservationFidelity.Fast), ct);
            var nextNormalized = SnapshotNormalizer.Normalize(next, options);
            count++;
            if (nextNormalized.IdentityHash == normalized.IdentityHash && !nextNormalized.StateDiscriminators.Contains(LoadingDiscriminator, StringComparer.Ordinal))
            {
                return new StableObservation(next, nextNormalized, count, Stable: true);
            }

            observation = next;
            normalized = nextNormalized;
        }

        return new StableObservation(observation, normalized, count, Stable: false);
    }
}
