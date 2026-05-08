using System;
using System.Runtime.InteropServices;

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
    public byte[] EntityHash { get; }
    public byte[] ContentHash { get; }

    public PhysicalityKey(string physicalityTypeCode, byte[] entityHash, byte[] contentHash)
    {
        ArgumentNullException.ThrowIfNull(physicalityTypeCode);
        ArgumentNullException.ThrowIfNull(entityHash);
        ArgumentNullException.ThrowIfNull(contentHash);
        PhysicalityTypeCode = physicalityTypeCode;
        EntityHash = entityHash;
        ContentHash = contentHash;
    }

    public bool Equals(PhysicalityKey other)
    {
        if (!string.Equals(PhysicalityTypeCode, other.PhysicalityTypeCode, StringComparison.Ordinal))
        {
            return false;
        }
        if (!ByteArrayEqual(EntityHash, other.EntityHash))
        {
            return false;
        }
        return ByteArrayEqual(ContentHash, other.ContentHash);
    }

    private static bool ByteArrayEqual(byte[]? a, byte[]? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null || a.Length != b.Length)
        {
            return false;
        }
        return MemoryExtensions.SequenceEqual<byte>(a, b);
    }

    public override bool Equals(object? obj) => obj is PhysicalityKey other && Equals(other);

    public override int GetHashCode()
    {
        int eh = EntityHash is { Length: >= 4 } ? MemoryMarshal.Read<int>(EntityHash.AsSpan(0, 4)) : 0;
        int ch = ContentHash is { Length: >= 4 } ? MemoryMarshal.Read<int>(ContentHash.AsSpan(0, 4)) : 0;
        return HashCode.Combine(PhysicalityTypeCode, eh, ch);
    }

    public static bool operator ==(PhysicalityKey left, PhysicalityKey right) => left.Equals(right);
    public static bool operator !=(PhysicalityKey left, PhysicalityKey right) => !left.Equals(right);
}
