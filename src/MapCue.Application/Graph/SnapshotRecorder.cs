using System.Text.Json;
using MapCue.Application.Ports;
using MapCue.Domain.Graph;

namespace MapCue.Application.Graph;

/// <summary>
/// Raw snapshot document stored as evidence (<c>mapcue.raw-snapshot/1</c>). <see cref="Payload"/> is the tool payload (already
/// redacted by the adapter) for lossless later analysis; <see cref="Elements"/> is what normalization reads.
/// </summary>
public sealed record RawSnapshotDocument(
    string SchemaVersion,
    string Device,
    string? ForegroundAppId,
    SnapshotFidelity Fidelity,
    string PayloadHash,
    DateTimeOffset ObservedAt,
    IReadOnlyList<RawElement> Elements,
    string? Payload)
{
    public const string CurrentSchemaVersion = "mapcue.raw-snapshot/1";
}

/// <summary>Normalized snapshot document stored as evidence (<c>mapcue.normalized-snapshot/1</c>).</summary>
public sealed record NormalizedSnapshotDocument(string SchemaVersion, NormalizedSnapshot Snapshot)
{
    public const string CurrentSchemaVersion = "mapcue.normalized-snapshot/1";
}

/// <summary>A snapshot whose artifacts are stored (before any database commit), ready to be committed in a context.</summary>
public sealed record RecordedSnapshot(UiSnapshot Snapshot, NormalizedSnapshot Normalized, IReadOnlyList<Evidence> Evidence);

/// <summary>
/// Stores an observation as evidence and normalizes it (architecture §12: artifacts are written before the commit).
/// Text fields are redacted first, the store redacts the whole document again, and normalization reads the document
/// <em>as stored</em>: the fingerprint can therefore always be recomputed from the stored blob (ADR-0006 §4).
/// </summary>
public sealed class SnapshotRecorder(IArtifactStore artifacts, IRedactor redactor, IClock clock)
{
    /// <summary>Version of the redaction rules applied to snapshots (bumped when <see cref="IRedactor"/> rules change).</summary>
    public const int RedactionVersion = 1;

    private static readonly RedactionContext _sink = new("snapshot");

    public async Task<RecordedSnapshot> RecordAsync(RawObservation observation, ExplorationContext context, IReadOnlyCollection<string>? inputValues, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        var fieldRedactions = new FieldRedactions();
        string? Redact(string? value)
        {
            if (value is null)
            {
                return null;
            }

            var result = redactor.Redact(value, _sink);
            fieldRedactions.Add(result);
            return result.Text;
        }

        RawElement RedactElement(RawElement element) => element with
        {
            ResourceId = Redact(element.ResourceId),
            Text = Redact(element.Text),
            AccessibilityText = Redact(element.AccessibilityText),
            ClassName = Redact(element.ClassName),
            HintText = Redact(element.HintText),
            Value = Redact(element.Value),
        };

        var fidelity = observation.Fidelity == ObservationFidelity.Full ? SnapshotFidelity.Full : SnapshotFidelity.Fast;
        var document = new RawSnapshotDocument(
            RawSnapshotDocument.CurrentSchemaVersion,
            Redact(observation.Device.Value)!,
            Redact(observation.ForegroundAppId),
            fidelity,
            observation.SnapshotHash,
            observation.ObservedAt,
            [.. observation.Elements.Select(RedactElement)],
            Redact(observation.RawPayload));

        var raw = await artifacts.PutTextAsync(GraphJson.Serialize(document), ArtifactKind.Snapshot, ct);
        raw = raw with { Redaction = fieldRedactions.MergedWith(raw.Redaction) };
        RawSnapshotDocument stored;
        try
        {
            stored = GraphJson.Deserialize<RawSnapshotDocument>(raw.RedactedText!);
        }
        catch (JsonException ex)
        {
            // Fail closed: never fall back to the unredacted elements.
            throw new InvalidOperationException("The stored snapshot is not valid JSON after redaction.", ex);
        }

        var normalized = SnapshotNormalizer.Normalize(stored.Elements, new NormalizationOptions(context.Platform, inputValues));
        var normalizedArtifact = await artifacts.PutTextAsync(
            GraphJson.Serialize(new NormalizedSnapshotDocument(NormalizedSnapshotDocument.CurrentSchemaVersion, normalized)),
            ArtifactKind.NormalizedSnapshot,
            ct);

        var key = ContentHash.Of("mapcue.snapshot/1", context.ContextId, raw.Ref.Sha256, fidelity.ToString(), normalized.FingerprintVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var snapshotId = ContentHash.ShortId("snap", key);
        var now = clock.UtcNow;
        var snapshot = new UiSnapshot(
            snapshotId,
            key,
            context.ContextId,
            AttemptId: null,
            fidelity,
            observation.SnapshotHash,
            raw.Ref.Sha256,
            normalizedArtifact.Ref.Sha256,
            normalized.IdentityHash,
            normalized.LayoutHash,
            normalized.ElementSetHash,
            normalized.FingerprintVersion,
            StateId: null,
            ScreenshotSha256: null,
            RedactionVersion,
            stored.ObservedAt);

        Evidence EvidenceOf(EvidenceKind kind, StoredArtifact artifact) => new(
            Evidence.IdOf(kind, artifact.Ref.Sha256, context.ContextId, null),
            kind,
            artifact.Ref.Sha256,
            artifact.Ref.Length,
            context.ContextId,
            snapshotId,
            AttemptId: null,
            artifact.Redaction.RedactionCount,
            artifact.Redaction.Rules,
            now);

        return new RecordedSnapshot(snapshot, normalized, [EvidenceOf(EvidenceKind.RawSnapshot, raw), EvidenceOf(EvidenceKind.NormalizedSnapshot, normalizedArtifact)]);
    }

    /// <summary>Redactions applied field by field before the store's own pass, reported on the raw snapshot evidence.</summary>
    private sealed class FieldRedactions
    {
        private readonly SortedSet<string> _rules = new(StringComparer.Ordinal);
        private int _count;

        public void Add(RedactedContent result)
        {
            _count += result.RedactionCount;
            _rules.UnionWith(result.RulesApplied);
        }

        public RedactionInfo MergedWith(RedactionInfo store) =>
            new(_count + store.RedactionCount, [.. _rules.Union(store.Rules, StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }
}
