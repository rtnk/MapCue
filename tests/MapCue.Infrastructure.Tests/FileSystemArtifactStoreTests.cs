using MapCue.Application.Ports;
using MapCue.Domain.Graph;
using MapCue.Infrastructure.Artifacts;
using MapCue.Infrastructure.Artifacts.Redaction;
using MapCue.Infrastructure.Tests.Graph;

namespace MapCue.Infrastructure.Tests;

public class FileSystemArtifactStoreTests
{
    private static FileSystemArtifactStore Create(out string root, PatternRedactor? redactor = null)
    {
        root = Path.Combine(GraphHarness.NewDirectory(), "blobs");
        return new FileSystemArtifactStore(root, redactor ?? new PatternRedactor());
    }

    [Fact]
    public async Task Text_is_redacted_before_it_is_hashed_and_written()
    {
        var redactor = new PatternRedactor();
        redactor.RegisterSensitiveValue("hunter2-secret");
        var store = Create(out var root, redactor);

        var stored = await store.PutTextAsync("user anna@example.com password hunter2-secret", ArtifactKind.ToolOutput, CancellationToken.None);

        Assert.Equal("user [REDACTED:email] password [REDACTED:secret]", stored.RedactedText);
        Assert.Equal(ContentHash.Sha256(stored.RedactedText!), stored.Ref.Sha256);
        Assert.Equal(2, stored.Redaction.RedactionCount);
        var file = store.PathOf(stored.Ref.Sha256);
        Assert.StartsWith(Path.Combine(root, "sha256", ContentHash.Hex(stored.Ref.Sha256)[..2]), file, StringComparison.Ordinal);
        Assert.Equal(stored.RedactedText, await File.ReadAllTextAsync(file));
        Assert.Equal(stored.RedactedText, await store.ReadTextAsync(stored.Ref.Sha256, CancellationToken.None));
    }

    [Fact]
    public async Task Identical_content_is_stored_once_and_no_temporary_files_remain()
    {
        var store = Create(out var root);
        var a = await store.PutTextAsync("{\"a\":1}", ArtifactKind.Snapshot, CancellationToken.None);
        var b = await store.PutTextAsync("{\"a\":1}", ArtifactKind.Snapshot, CancellationToken.None);

        Assert.Equal(a.Ref, b.Ref);
        Assert.True(store.Exists(a.Ref.Sha256));
        Assert.Single(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(root, ".tmp-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("sha256:../../../etc/passwd")]
    [InlineData("../x")]
    [InlineData("sha256:ABCDEF")]
    public async Task Malformed_references_are_rejected(string reference)
    {
        var store = Create(out _);
        Assert.Throws<ArgumentException>(() => store.Exists(reference));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadTextAsync(reference, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_artifact_reads_as_null()
    {
        var store = Create(out _);
        Assert.Null(await store.ReadTextAsync(ContentHash.Sha256("nothing"), CancellationToken.None));
    }

    [Fact]
    public async Task Only_screenshots_may_bypass_text_redaction()
    {
        var store = Create(out _);
        using var bytes = new MemoryStream([1, 2, 3]);
        var screenshot = await store.PutBinaryAsync(bytes, ArtifactKind.Screenshot, RedactionInfo.None, CancellationToken.None);
        Assert.Equal(3, screenshot.Ref.Length);
        Assert.Null(screenshot.RedactedText);

        using var other = new MemoryStream([1]);
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutBinaryAsync(other, ArtifactKind.Snapshot, RedactionInfo.None, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutTextAsync("x", ArtifactKind.Screenshot, CancellationToken.None));
    }
}
