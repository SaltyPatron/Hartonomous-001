using System;
using System.Runtime.InteropServices;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// PK shape for <c>substrate.sequence</c>: (parent_hash, ordinal). RLE
/// collapses contiguous identical children into a single row at ordinal N
/// with rle_count=K, so probing whether a parent has a row at ordinal N is
/// the existence question — child_hash and rle_count are not part of the PK.
/// </summary>
public readonly struct SequenceKey : IEquatable<SequenceKey>
{
    public byte[] ParentHash { get; }
    public int Ordinal { get; }

    public SequenceKey(byte[] parentHash, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(parentHash);
        ParentHash = parentHash;
        Ordinal = ordinal;
    }

    public bool Equals(SequenceKey other)
    {
        if (Ordinal != other.Ordinal)
        {
            return false;
        }
        if (ReferenceEquals(ParentHash, other.ParentHash))
        {
            return true;
        }
        if (ParentHash is null || other.ParentHash is null)
        {
            return false;
        }
        if (ParentHash.Length != other.ParentHash.Length)
        {
            return false;
        }
        return MemoryExtensions.SequenceEqual<byte>(ParentHash, other.ParentHash);
    }

    public override bool Equals(object? obj) => obj is SequenceKey other && Equals(other);

    public override int GetHashCode()
    {
        int h = ParentHash is { Length: >= 4 } ? MemoryMarshal.Read<int>(ParentHash.AsSpan(0, 4)) : 0;
        return HashCode.Combine(h, Ordinal);
    }

    public static bool operator ==(SequenceKey left, SequenceKey right) => left.Equals(right);
    public static bool operator !=(SequenceKey left, SequenceKey right) => !left.Equals(right);
}
