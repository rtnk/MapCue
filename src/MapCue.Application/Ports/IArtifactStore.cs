namespace MapCue.Application.Ports;

/// <summary>Content-addressed artifact store (ADR-0003). Content must be redacted before it is put.</summary>
public interface IArtifactStore
{
    Task<ArtifactRef> PutAsync(Stream content, ArtifactKind kind, RedactionInfo redaction, CancellationToken ct);
}

public enum ArtifactKind
{
    Snapshot,
    Screenshot,
    AgentRequest,
    AgentResponse,
    ToolOutput,
    Flow,
    Report,
}

/// <summary>Reference to a stored blob, e.g. <c>sha256:ab…</c>.</summary>
public sealed record ArtifactRef(string Sha256, long Length, ArtifactKind Kind);

/// <summary>What redaction was applied to the content before storage.</summary>
public sealed record RedactionInfo(int RedactionCount, IReadOnlyList<string> Rules);
