using System;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// PK shape for <c>substrate.sequence</c>: (parent_hash, ordinal). RLE
/// collapses contiguous identical children into a single row at ordinal N
/// with rle_count=K, so probing whether a parent has a row at ordinal N is
/// the existence question — child_hash and rle_count are not part of the PK.
/// </summary>
public readonly struct SequenceKey : IEquatable<SequenceKey>
{
    public Hash32 ParentHash { get; }
    public int Ordinal { get; }

    public SequenceKey(Hash32 parentHash, int ordinal)
    {
        ParentHash = parentHash;
        Ordinal = ordinal;
    }

    public SequenceKey(byte[] parentHash, int ordinal)
        : this(new Hash32(parentHash), ordinal)
    {
    }

    public bool Equals(SequenceKey other)
    {
        if (Ordinal != other.Ordinal)
        {
            return false;
        }
        return ParentHash.Equals(other.ParentHash);
    }

    public override bool Equals(object? obj) => obj is SequenceKey other && Equals(other);

    public override int GetHashCode()
    {
        return HashCode.Combine(ParentHash, Ordinal);
    }

    public static bool operator ==(SequenceKey left, SequenceKey right) => left.Equals(right);
    public static bool operator !=(SequenceKey left, SequenceKey right) => !left.Equals(right);
}
