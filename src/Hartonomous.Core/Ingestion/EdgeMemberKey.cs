using System;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// PK shape for <c>substrate.edge_member</c> at the producer side.
/// Substrate-side codes resolve to integer reference IDs at SQL time.
/// </summary>
public readonly struct EdgeMemberKey : IEquatable<EdgeMemberKey>
{
    public string EdgeTypeCode { get; }
    public Hash32 EdgeHash { get; }
    public Hash32 EntityHash { get; }
    public string RoleCode { get; }
    public int RolePosition { get; }

    public EdgeMemberKey(
        string edgeTypeCode,
        Hash32 edgeHash,
        Hash32 entityHash,
        string roleCode,
        int rolePosition)
    {
        ArgumentNullException.ThrowIfNull(edgeTypeCode);
        ArgumentNullException.ThrowIfNull(roleCode);
        EdgeTypeCode = edgeTypeCode;
        EdgeHash = edgeHash;
        EntityHash = entityHash;
        RoleCode = roleCode;
        RolePosition = rolePosition;
    }

    public EdgeMemberKey(
        string edgeTypeCode,
        byte[] edgeHash,
        byte[] entityHash,
        string roleCode,
        int rolePosition)
        : this(edgeTypeCode, new Hash32(edgeHash), new Hash32(entityHash), roleCode, rolePosition)
    {
    }

    public bool Equals(EdgeMemberKey other)
    {
        return RolePosition == other.RolePosition
            && string.Equals(EdgeTypeCode, other.EdgeTypeCode, StringComparison.Ordinal)
            && string.Equals(RoleCode, other.RoleCode, StringComparison.Ordinal)
            && EdgeHash.Equals(other.EdgeHash)
            && EntityHash.Equals(other.EntityHash);
    }

    public override bool Equals(object? obj) => obj is EdgeMemberKey other && Equals(other);

    public override int GetHashCode()
    {
        return HashCode.Combine(EdgeHash, EntityHash, EdgeTypeCode, RoleCode, RolePosition);
    }

    public static bool operator ==(EdgeMemberKey left, EdgeMemberKey right) => left.Equals(right);
    public static bool operator !=(EdgeMemberKey left, EdgeMemberKey right) => !left.Equals(right);
}
