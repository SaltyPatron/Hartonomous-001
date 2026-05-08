using System;
using System.Runtime.InteropServices;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// Wraps a 32-byte BLAKE3 hash with structural equality + hashing so byte[]
/// instances can be used as HashSet/Dictionary keys without reference-equality
/// surprises. Decomposer code that builds candidate-PK sets uses this as the
/// element type for entity-hash bulk checks.
/// </summary>
public readonly struct HashKey : IEquatable<HashKey>
{
    public byte[] Hash { get; }

    public HashKey(byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        Hash = hash;
    }

    public bool Equals(HashKey other)
    {
        if (ReferenceEquals(Hash, other.Hash))
        {
            return true;
        }
        if (Hash is null || other.Hash is null || Hash.Length != other.Hash.Length)
        {
            return false;
        }
        return MemoryExtensions.SequenceEqual<byte>(Hash, other.Hash);
    }

    public override bool Equals(object? obj) => obj is HashKey other && Equals(other);

    public override int GetHashCode()
    {
        if (Hash is null || Hash.Length < 4)
        {
            return 0;
        }
        // First four bytes of BLAKE3 are uniform-random — direct int read is
        // a well-distributed hash code without further mixing.
        return MemoryMarshal.Read<int>(Hash.AsSpan(0, 4));
    }

    public static bool operator ==(HashKey left, HashKey right) => left.Equals(right);
    public static bool operator !=(HashKey left, HashKey right) => !left.Equals(right);
}
