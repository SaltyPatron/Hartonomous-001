using System;
using Hartonomous.Core.Compute.Common;

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
    public Hash32 EdgeHash { get; }

    public EdgeKey(string edgeTypeCode, Hash32 edgeHash)
    {
        ArgumentNullException.ThrowIfNull(edgeTypeCode);
        EdgeTypeCode = edgeTypeCode;
        EdgeHash = edgeHash;
    }

    public EdgeKey(string edgeTypeCode, byte[] edgeHash)
        : this(edgeTypeCode, new Hash32(edgeHash))
    {
    }

    public bool Equals(EdgeKey other)
    {
        if (!string.Equals(EdgeTypeCode, other.EdgeTypeCode, StringComparison.Ordinal))
        {
            return false;
        }
        return EdgeHash.Equals(other.EdgeHash);
    }

    public override bool Equals(object? obj) => obj is EdgeKey other && Equals(other);

    public override int GetHashCode()
    {
        return HashCode.Combine(EdgeHash, EdgeTypeCode);
    }

    public static bool operator ==(EdgeKey left, EdgeKey right) => left.Equals(right);
    public static bool operator !=(EdgeKey left, EdgeKey right) => !left.Equals(right);
}
