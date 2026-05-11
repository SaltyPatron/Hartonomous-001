using System;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// PK shape for <c>substrate.physicality</c> at the producer side:
/// (physicality_type_code, entity_hash, content_hash). Same content geometry
/// (e.g. identical POINTZM) on different entities is not duplicate; same
/// content geometry on the same entity under the same type IS duplicate.
/// </summary>
public readonly struct PhysicalityKey : IEquatable<PhysicalityKey>
{
    public string PhysicalityTypeCode { get; }
    public Hash32 EntityHash { get; }
    public Hash32 ContentHash { get; }

    public PhysicalityKey(string physicalityTypeCode, Hash32 entityHash, Hash32 contentHash)
    {
        ArgumentNullException.ThrowIfNull(physicalityTypeCode);
        PhysicalityTypeCode = physicalityTypeCode;
        EntityHash = entityHash;
        ContentHash = contentHash;
    }

    public PhysicalityKey(string physicalityTypeCode, byte[] entityHash, byte[] contentHash)
        : this(physicalityTypeCode, new Hash32(entityHash), new Hash32(contentHash))
    {
    }

    public bool Equals(PhysicalityKey other)
    {
        if (!string.Equals(PhysicalityTypeCode, other.PhysicalityTypeCode, StringComparison.Ordinal))
        {
            return false;
        }
        if (!EntityHash.Equals(other.EntityHash))
        {
            return false;
        }
        return ContentHash.Equals(other.ContentHash);
    }

    public override bool Equals(object? obj) => obj is PhysicalityKey other && Equals(other);

    public override int GetHashCode()
    {
        return HashCode.Combine(PhysicalityTypeCode, EntityHash, ContentHash);
    }

    public static bool operator ==(PhysicalityKey left, PhysicalityKey right) => left.Equals(right);
    public static bool operator !=(PhysicalityKey left, PhysicalityKey right) => !left.Equals(right);
}
