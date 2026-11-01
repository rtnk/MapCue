namespace MapCue.Application.Ports;

/// <summary>
/// Content-addressed artifact store (ADR-0003, architecture §12). Text is redacted by the store itself (<see cref="IRedactor"/>)
/// before it is hashed and written, so nothing unredacted reaches the disk and the address is the hash of the redacted bytes.
/// Writes are durable (temporary file, flush to disk, rename) before the call returns, i.e. before any database commit refers to them.
/// </summary>
public interface IArtifactStore
{
    /// <summary>Redacts <paramref name="content"/>, then stores its UTF-8 bytes. Storing the same redacted content twice is a no-op.</summary>
    Task<StoredArtifact> PutTextAsync(string content, ArtifactKind kind, CancellationToken ct);

    /// <summary>
    /// Stores binary content that cannot be text-redacted (screenshots, P1). Only <see cref="ArtifactKind.Screenshot"/> is accepted and
    /// the caller states what redaction it applied (architecture §10.3: screenshots only for synthetic-data profiles until masking exists).
    /// </summary>
    Task<StoredArtifact> PutBinaryAsync(Stream content, ArtifactKind kind, RedactionInfo redaction, CancellationToken ct);

    /// <summary>Reads a stored text artifact, or null when it does not exist.</summary>
    Task<string?> ReadTextAsync(string sha256, CancellationToken ct);

    bool Exists(string sha256);
}

public enum ArtifactKind
{
    Snapshot,
    NormalizedSnapshot,
    Screenshot,
    AgentRequest,
    AgentResponse,
    ToolOutput,
    Flow,
    Report,

    /// <summary>Keyboard focus probe of a state (STEP-08, <c>mapcue.focus-probe/1</c>).</summary>
    FocusProbe,

    /// <summary>Evidence file of a manual accessibility record by the operator (STEP-08, <c>mapcue a11y record</c>).</summary>
    OperatorRecord,

    /// <summary>A screen contract version (STEP-08, <c>mapcue.screen-contract/1</c>).</summary>
    ScreenContract,
}

/// <summary>Reference to a stored blob; <see cref="Sha256"/> is <c>sha256:&lt;hex&gt;</c> of the stored (redacted) bytes.</summary>
public sealed record ArtifactRef(string Sha256, long Length, ArtifactKind Kind);

/// <summary>What redaction was applied to the content before storage.</summary>
public sealed record RedactionInfo(int RedactionCount, IReadOnlyList<string> Rules)
{
    public static RedactionInfo None { get; } = new(0, []);
}

/// <summary>A stored artifact. <see cref="RedactedText"/> is the text as stored (null for binary content).</summary>
public sealed record StoredArtifact(ArtifactRef Ref, RedactionInfo Redaction, string? RedactedText);
