using System.Text;
using System.Text.RegularExpressions;
using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.ScreenContracts;

/// <summary>What the operator records (<c>mapcue a11y record</c>). <see cref="Screen"/> is a screen key or id; <see cref="Profile"/> picks the context.</summary>
public sealed record AccessibilityRecordRequest(
    string Screen,
    string? Profile,
    string? ContextId,
    string? StateId,
    AccessibilityTechnology Technology,
    string? ElementKey,
    string Property,
    AccessibilityRecordResult Result,
    string Operator,
    string Device,
    string EvidenceText,
    string? Note);

/// <summary>Why a manual record was refused.</summary>
public sealed class AccessibilityRecordException(string message) : Exception(message);

/// <summary>
/// Stores a manual accessibility check of the operator (STEP-08, <c>docs/a11y-manual-protocol.md</c>): the only way to the
/// <c>assistiveVerified</c> level for screen readers (G-05). It refuses records that cannot be true (TalkBack on iOS, VoiceOver on Android,
/// keyboard on iOS: G-04), screens, states or contexts the workspace does not know, and records without evidence. The evidence text is
/// redacted by the artifact store before it is written; operator, device and note are redacted too.
/// </summary>
public static partial class AccessibilityRecorder
{
    public const string ProtocolVersion = "mapcue.a11y-protocol/1";

    public const int MaxEvidenceBytes = 1024 * 1024;

    public static async Task<AccessibilityRecord> RecordAsync(IStateStore store, IArtifactStore artifacts, IRedactor redactor, IClock clock, AccessibilityRecordRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(request);

        if (!A11yProperties.Known.Contains(request.Property))
        {
            throw new AccessibilityRecordException($"--property must be one of: {string.Join(", ", A11yProperties.Known.Order(StringComparer.Ordinal))}.");
        }

        if (request.Property == A11yProperties.Screen && request.ElementKey is not null)
        {
            throw new AccessibilityRecordException($"--property {A11yProperties.Screen} is a whole-screen record; omit --element.");
        }

        if (string.IsNullOrWhiteSpace(request.EvidenceText) || Encoding.UTF8.GetByteCount(request.EvidenceText) > MaxEvidenceBytes)
        {
            throw new AccessibilityRecordException($"The evidence file must be a non-empty UTF-8 text file of at most {MaxEvidenceBytes} bytes (the protocol transcript).");
        }

        if (!ShortText().IsMatch(request.Operator) || !ShortText().IsMatch(request.Device))
        {
            throw new AccessibilityRecordException("--operator and --device must be 1-200 printable characters.");
        }

        var resolved = await store.InTransactionAsync(
            async uow =>
            {
                var screen = (await uow.Graph.ListScreensAsync(ct)).FirstOrDefault(s => s.ScreenKey == request.Screen || s.ScreenId == request.Screen)
                    ?? throw new AccessibilityRecordException($"Unknown screen '{request.Screen}' (use a screen key or id of this workspace).");
                var states = (await uow.Graph.ListStatesAsync(ct)).Where(s => s.ScreenId == screen.ScreenId).Select(s => s.StateId).ToHashSet(StringComparer.Ordinal);
                if (request.StateId is not null && !states.Contains(request.StateId))
                {
                    throw new AccessibilityRecordException($"State '{request.StateId}' is not a state of screen '{screen.ScreenKey}'.");
                }

                var variants = (await uow.Graph.ListVariantsAsync(ct)).Where(v => states.Contains(v.StateId) && (request.StateId is null || v.StateId == request.StateId)).ToList();
                var contexts = (await uow.Graph.ListContextsAsync(ct)).Where(c => variants.Any(v => v.ContextId == c.ContextId)).OrderBy(c => c.ContextId, StringComparer.Ordinal).ToList();
                var candidates = contexts.Where(c => (request.ContextId is null || c.ContextId == request.ContextId) && (request.Profile is null || c.ProfileId == request.Profile)).ToList();
                return candidates.Count switch
                {
                    0 => throw new AccessibilityRecordException($"Screen '{screen.ScreenKey}' was never seen in the requested context or profile."),
                    > 1 => throw new AccessibilityRecordException($"Screen '{screen.ScreenKey}' was seen in {candidates.Count} contexts ({string.Join(", ", candidates.Select(c => $"{c.ContextId} profile={c.ProfileId} build={c.AppBuildId}"))}); choose one with --context."),
                    _ => (Screen: screen, Context: candidates[0]),
                };
            },
            ct);

        var platform = resolved.Context.Platform;
        var valid = (request.Technology, platform) switch
        {
            (AccessibilityTechnology.TalkBack, Platform.Android) => true,
            (AccessibilityTechnology.VoiceOver, Platform.Ios) => true,
            (AccessibilityTechnology.Keyboard, Platform.Android) => true,
            _ => false,
        };
        if (!valid)
        {
            throw new AccessibilityRecordException(request.Technology == AccessibilityTechnology.Keyboard
                ? "Keyboard records on iOS are not supported (G-04: no Tab key through TapCue; the contract cell is unsupported)."
                : $"{request.Technology} cannot be recorded on the {platform} platform.");
        }

        var evidence = await artifacts.PutTextAsync(request.EvidenceText, ArtifactKind.OperatorRecord, ct);
        var now = clock.UtcNow;
        var contextId = resolved.Context.ContextId;
        var evidenceRow = new Evidence(
            Evidence.IdOf(EvidenceKind.OperatorRecord, evidence.Ref.Sha256, contextId, null),
            EvidenceKind.OperatorRecord,
            evidence.Ref.Sha256,
            evidence.Ref.Length,
            contextId,
            null,
            null,
            evidence.Redaction.RedactionCount,
            evidence.Redaction.Rules,
            now);
        string Redact(string text) => redactor.Redact(text, new RedactionContext("a11y-record")).Text;
        var record = new AccessibilityRecord(
            ContentHash.ShortId("a11y", ContentHash.Of("mapcue.a11y-record/1", resolved.Screen.ScreenId, request.StateId, contextId, request.Technology.ToString(), request.ElementKey, request.Property, request.Result.ToString(), evidence.Ref.Sha256)),
            resolved.Screen.ScreenId,
            request.StateId,
            contextId,
            request.Technology,
            request.ElementKey,
            request.Property,
            request.Result,
            Redact(request.Operator),
            Redact(request.Device),
            ProtocolVersion,
            request.Note is null ? null : Redact(request.Note),
            evidenceRow.EvidenceId,
            now);
        await store.InTransactionAsync(
            async uow =>
            {
                await uow.Graph.InsertEvidenceIfAbsentAsync(evidenceRow, ct);
                await uow.Contracts.InsertRecordAsync(record, ct);
                return 0;
            },
            ct);
        return record;
    }

    [GeneratedRegex(@"^[^\p{C}]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortText();
}
