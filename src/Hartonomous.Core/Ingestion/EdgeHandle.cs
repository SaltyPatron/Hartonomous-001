using System;
using Hartonomous.Core.Compute.Common;

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
    public Hash32 Hash { get; }
    public string EdgeTypeCode { get; }

    public EdgeHandle(Hash32 hash, string edgeTypeCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(edgeTypeCode);
        Hash = hash;
        EdgeTypeCode = edgeTypeCode;
    }

    public EdgeHandle(byte[] hash, string edgeTypeCode)
        : this(new Hash32(hash), edgeTypeCode)
    {
    }

    public bool Equals(EdgeHandle other)
    {
        if (!string.Equals(EdgeTypeCode, other.EdgeTypeCode, StringComparison.Ordinal))
        {
            return false;
        }
        return Hash.Equals(other.Hash);
    }

    public override bool Equals(object? obj) => obj is EdgeHandle h && Equals(h);

    public override int GetHashCode()
    {
        return HashCode.Combine(EdgeTypeCode, Hash);
    }

    public static bool operator ==(EdgeHandle left, EdgeHandle right) => left.Equals(right);
    public static bool operator !=(EdgeHandle left, EdgeHandle right) => !left.Equals(right);

    public override string ToString()
    {
        return $"{EdgeTypeCode}/{Hash.ToHexString()[..16]}...";
    }
}
