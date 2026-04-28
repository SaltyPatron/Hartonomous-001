using System;

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
    public byte[] Hash { get; }
    public string EntityTypeCode { get; }

    public EntityHandle(byte[] hash, string entityTypeCode)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentException.ThrowIfNullOrEmpty(entityTypeCode);
        if (hash.Length != 32)
        {
            throw new ArgumentException(
                $"EntityHandle requires a 32-byte BLAKE3 hash; got {hash.Length} bytes.",
                nameof(hash));
        }
        Hash = hash;
        EntityTypeCode = entityTypeCode;
    }

    public bool Equals(EntityHandle other)
    {
        if (!string.Equals(EntityTypeCode, other.EntityTypeCode, StringComparison.Ordinal))
        {
            return false;
        }
        return Hash.AsSpan().SequenceEqual(other.Hash.AsSpan());
    }

    public override bool Equals(object? obj) => obj is EntityHandle h && Equals(h);

    public override int GetHashCode()
    {
        // BLAKE3 output is uniformly distributed; mix the type code with the
        // first 8 bytes of the hash for a fast, well-spread hash code.
        ReadOnlySpan<byte> span = Hash;
        long head = BitConverter.ToInt64(span);
        return HashCode.Combine(EntityTypeCode, head);
    }

    public static bool operator ==(EntityHandle left, EntityHandle right) => left.Equals(right);
    public static bool operator !=(EntityHandle left, EntityHandle right) => !left.Equals(right);

    public override string ToString()
    {
        // Compact debug rendering: "type_code/<first-8-hex>...".
        ReadOnlySpan<byte> span = Hash;
        return $"{EntityTypeCode}/{Convert.ToHexString(span[..8]).ToLowerInvariant()}...";
    }
}
