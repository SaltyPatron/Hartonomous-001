using System;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// A reference to a substrate entity by its content-addressed identity:
/// the BLAKE3 hash of its content plus the entity_type_code that selects
/// which partition of substrate.entity it belongs to.
///
/// In the hash-as-PK substrate this struct IS the foreign key. There is
/// no surrogate id, no per-batch BatchIndex, no remap step. The decomposer
/// computes the hash once (via Blake3.Hash) and the same handle flows
/// through every downstream write — edge_member, junction, physicality,
/// sequence, significance, entity_model_source — without ever touching a
/// BIGINT.
///
/// Equality is value-based on (EntityTypeCode, Hash content). Two handles
/// produced from the same input bytes are equal even if they came from
/// different decomposers in different batches.
/// </summary>
public readonly struct EntityHandle : IEquatable<EntityHandle>
{
    public Hash32 Hash { get; }
    public string EntityTypeCode { get; }

    public EntityHandle(Hash32 hash, string entityTypeCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(entityTypeCode);
        Hash = hash;
        EntityTypeCode = entityTypeCode;
    }

    public EntityHandle(byte[] hash, string entityTypeCode)
        : this(new Hash32(hash), entityTypeCode)
    {
    }

    public bool Equals(EntityHandle other)
    {
        if (!string.Equals(EntityTypeCode, other.EntityTypeCode, StringComparison.Ordinal))
        {
            return false;
        }
        return Hash.Equals(other.Hash);
    }

    public override bool Equals(object? obj) => obj is EntityHandle h && Equals(h);

    public override int GetHashCode()
    {
        // BLAKE3 output is uniformly distributed; mix the type code with the
        // first 8 bytes of the hash for a fast, well-spread hash code.
        return HashCode.Combine(EntityTypeCode, Hash);
    }

    public static bool operator ==(EntityHandle left, EntityHandle right) => left.Equals(right);
    public static bool operator !=(EntityHandle left, EntityHandle right) => !left.Equals(right);

    public override string ToString()
    {
        // Compact debug rendering: "type_code/<first-8-hex>...".
        return $"{EntityTypeCode}/{Hash.ToHexString()[..16]}...";
    }
}
