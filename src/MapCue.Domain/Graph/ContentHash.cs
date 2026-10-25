using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MapCue.Domain.Graph;

/// <summary>SHA-256 helpers. Hashes are written as <c>sha256:&lt;lowercase hex&gt;</c>.</summary>
public static class ContentHash
{
    public const string Prefix = "sha256:";

    private static readonly System.Buffers.SearchValues<char> _hexDigits = System.Buffers.SearchValues.Create("0123456789abcdef");

    public static string Sha256(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Prefix + Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Hash of an ordered tuple of parts. Every part is length-prefixed, so <c>("ab", "c")</c> and <c>("a", "bc")</c>
    /// never collide. A null part is distinct from an empty one.
    /// </summary>
    public static string Of(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (part is null)
            {
                builder.Append("-1:;");
                continue;
            }

            builder.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part).Append(';');
        }

        return Sha256(builder.ToString());
    }

    /// <summary>Hex part of a hash (without <see cref="Prefix"/>).</summary>
    public static string Hex(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return hash.StartsWith(Prefix, StringComparison.Ordinal) ? hash[Prefix.Length..] : hash;
    }

    /// <summary>True for <c>sha256:</c> followed by exactly 64 lowercase hex characters.</summary>
    public static bool IsValid(string? hash) =>
        hash is not null
        && hash.Length == Prefix.Length + 64
        && hash.StartsWith(Prefix, StringComparison.Ordinal)
        && hash.AsSpan(Prefix.Length).ContainsAnyExcept(_hexDigits) is false;

    /// <summary>A short deterministic identifier derived from a hash, e.g. <c>ctx_1a2b3c4d5e6f7a8b</c>.</summary>
    public static string ShortId(string prefix, string hash, int hexLength = 16)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return $"{prefix}_{Hex(hash)[..hexLength]}";
    }
}
