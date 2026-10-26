using System.Security.Cryptography;
using System.Text;
using MapCue.Application.Ports;
using MapCue.Domain.Graph;

namespace MapCue.Infrastructure.Artifacts;

/// <summary>
/// Content-addressed blobs under <c>&lt;root&gt;/sha256/ab/&lt;hex&gt;</c> (architecture §12). Text is redacted with <see cref="IRedactor"/>
/// before hashing; the blob is written to a temporary file in the same directory, flushed to disk and renamed, so a reader never sees
/// a partial blob and a crash leaves at most a temporary file. Identical content is stored once.
/// </summary>
public sealed class FileSystemArtifactStore : IArtifactStore
{
    private static readonly RedactionContext _sink = new("artifact");
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _root;
    private readonly IRedactor _redactor;

    public FileSystemArtifactStore(string root, IRedactor redactor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(redactor);
        _root = Path.GetFullPath(root);
        _redactor = redactor;
    }

    public string Root => _root;

    public async Task<StoredArtifact> PutTextAsync(string content, ArtifactKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (kind == ArtifactKind.Screenshot)
        {
            throw new ArgumentException("Screenshots are binary; use PutBinaryAsync.", nameof(kind));
        }

        var redacted = _redactor.Redact(content, _sink);
        var bytes = _utf8.GetBytes(redacted.Text);
        var reference = await WriteAsync(bytes, kind, ct);
        return new StoredArtifact(reference, new RedactionInfo(redacted.RedactionCount, redacted.RulesApplied), redacted.Text);
    }

    public async Task<StoredArtifact> PutBinaryAsync(Stream content, ArtifactKind kind, RedactionInfo redaction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(redaction);
        if (kind != ArtifactKind.Screenshot)
        {
            throw new ArgumentException($"Only screenshots may be stored as binary; {kind} must go through PutTextAsync (redaction).", nameof(kind));
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var reference = await WriteAsync(buffer.ToArray(), kind, ct);
        return new StoredArtifact(reference, redaction, null);
    }

    public async Task<string?> ReadTextAsync(string sha256, CancellationToken ct)
    {
        var path = PathOf(sha256);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, _utf8, ct) : null;
    }

    public bool Exists(string sha256) => File.Exists(PathOf(sha256));

    /// <summary>Path of a blob. Only well-formed <c>sha256:&lt;64 hex&gt;</c> references are accepted (no path traversal).</summary>
    public string PathOf(string sha256)
    {
        if (!ContentHash.IsValid(sha256))
        {
            throw new ArgumentException("Artifact reference must be sha256:<64 lowercase hex>.", nameof(sha256));
        }

        var hex = ContentHash.Hex(sha256);
        return Path.Combine(_root, "sha256", hex[..2], hex);
    }

    private async Task<ArtifactRef> WriteAsync(byte[] bytes, ArtifactKind kind, CancellationToken ct)
    {
        var sha = ContentHash.Prefix + Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = PathOf(sha);
        if (File.Exists(path) && new FileInfo(path).Length == bytes.Length)
        {
            return new ArtifactRef(sha, bytes.Length, kind);
        }

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".tmp-{Path.GetFileName(path)}-{Guid.NewGuid():N}");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        return new ArtifactRef(sha, bytes.Length, kind);
    }
}
