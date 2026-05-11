using System;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// Wraps a 32-byte BLAKE3 hash with structural equality + hashing so byte[]
/// instances can be used as HashSet/Dictionary keys without reference-equality
/// surprises. Decomposer code that builds candidate-PK sets uses this as the
/// element type for entity-hash bulk checks.
/// </summary>
public readonly struct HashKey : IEquatable<HashKey>
{
    public Hash32 Hash { get; }

    public HashKey(Hash32 hash)
    {
        Hash = hash;
    }

    public HashKey(byte[] hash) : this(new Hash32(hash)) { }

    public bool Equals(HashKey other)
    {
        return Hash.Equals(other.Hash);
    }

    public override bool Equals(object? obj) => obj is HashKey other && Equals(other);

    public override int GetHashCode()
    {
        return Hash.GetHashCode();
    }

    public static bool operator ==(HashKey left, HashKey right) => left.Equals(right);
    public static bool operator !=(HashKey left, HashKey right) => !left.Equals(right);
}
