using System;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// A reference to a substrate edge by its content-addressed identity:
/// the BLAKE3 hash of (edge_type_id, role-ordered participant hashes) plus
/// the edge_type_code that selects which partition of substrate.edge it
/// belongs to.
///
/// Mirrors <see cref="EntityHandle"/> for edges. In the hash-as-PK substrate
/// this struct IS the foreign key. There is no surrogate id. Two edges
/// produced from the same (edge_type, ordered participants) by different
/// decomposers in different batches are equal.
///
/// Equality is value-based on (EdgeTypeCode, Hash content).
/// </summary>
public readonly struct EdgeHandle : IEquatable<EdgeHandle>
{
    public byte[] Hash { get; }
    public string EdgeTypeCode { get; }

    public EdgeHandle(byte[] hash, string edgeTypeCode)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentException.ThrowIfNullOrEmpty(edgeTypeCode);
        if (hash.Length != 32)
        {
            throw new ArgumentException(
                $"EdgeHandle requires a 32-byte BLAKE3 hash; got {hash.Length} bytes.",
                nameof(hash));
        }
        Hash = hash;
        EdgeTypeCode = edgeTypeCode;
    }

    public bool Equals(EdgeHandle other)
    {
        if (!string.Equals(EdgeTypeCode, other.EdgeTypeCode, StringComparison.Ordinal))
        {
            return false;
        }
        return Hash.AsSpan().SequenceEqual(other.Hash.AsSpan());
    }

    public override bool Equals(object? obj) => obj is EdgeHandle h && Equals(h);

    public override int GetHashCode()
    {
        ReadOnlySpan<byte> span = Hash;
        long head = BitConverter.ToInt64(span);
        return HashCode.Combine(EdgeTypeCode, head);
    }

    public static bool operator ==(EdgeHandle left, EdgeHandle right) => left.Equals(right);
    public static bool operator !=(EdgeHandle left, EdgeHandle right) => !left.Equals(right);

    public override string ToString()
    {
        ReadOnlySpan<byte> span = Hash;
        return $"{EdgeTypeCode}/{Convert.ToHexString(span[..8]).ToLowerInvariant()}...";
    }
}
