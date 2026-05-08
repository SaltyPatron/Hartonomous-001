using System;
using System.Runtime.InteropServices;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// PK shape for <c>substrate.edge</c> at the producer side: (edge_type_code,
/// edge_hash). The substrate-side PK is (edge_type_id, edge_hash) — the code
/// resolves to id at SQL time. Decomposers compute the edge_hash via
/// <c>BaseDecomposer.ComputeEdgeHash</c> from the role-ordered participant
/// hashes plus the edge_type_id.
/// </summary>
public readonly struct EdgeKey : IEquatable<EdgeKey>
{
    public string EdgeTypeCode { get; }
    public byte[] EdgeHash { get; }

    public EdgeKey(string edgeTypeCode, byte[] edgeHash)
    {
        ArgumentNullException.ThrowIfNull(edgeTypeCode);
        ArgumentNullException.ThrowIfNull(edgeHash);
        EdgeTypeCode = edgeTypeCode;
        EdgeHash = edgeHash;
    }

    public bool Equals(EdgeKey other)
    {
        if (!string.Equals(EdgeTypeCode, other.EdgeTypeCode, StringComparison.Ordinal))
        {
            return false;
        }
        if (ReferenceEquals(EdgeHash, other.EdgeHash))
        {
            return true;
        }
        if (EdgeHash is null || other.EdgeHash is null)
        {
            return false;
        }
        if (EdgeHash.Length != other.EdgeHash.Length)
        {
            return false;
        }
        return MemoryExtensions.SequenceEqual<byte>(EdgeHash, other.EdgeHash);
    }

    public override bool Equals(object? obj) => obj is EdgeKey other && Equals(other);

    public override int GetHashCode()
    {
        int h = EdgeHash is { Length: >= 4 } ? MemoryMarshal.Read<int>(EdgeHash.AsSpan(0, 4)) : 0;
        return HashCode.Combine(h, EdgeTypeCode);
    }

    public static bool operator ==(EdgeKey left, EdgeKey right) => left.Equals(right);
    public static bool operator !=(EdgeKey left, EdgeKey right) => !left.Equals(right);
}
