using System;
using System.Runtime.InteropServices;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// PK shape for <c>substrate.entity_classification</c> at the producer side
/// where reference codes haven't yet been resolved to IDs. Decomposers
/// build sets of these from precomputed (hash, type, provenance) candidates
/// and pass them to <c>IIngestionPipeline.GetExistingEntityClassificationsAsync</c>.
/// </summary>
public readonly struct EntityClassificationKey : IEquatable<EntityClassificationKey>
{
    public byte[] EntityHash { get; }
    public string EntityTypeCode { get; }
    public string ProvenanceCode { get; }

    public EntityClassificationKey(byte[] entityHash, string entityTypeCode, string provenanceCode)
    {
        ArgumentNullException.ThrowIfNull(entityHash);
        ArgumentNullException.ThrowIfNull(entityTypeCode);
        ArgumentNullException.ThrowIfNull(provenanceCode);
        EntityHash = entityHash;
        EntityTypeCode = entityTypeCode;
        ProvenanceCode = provenanceCode;
    }

    public bool Equals(EntityClassificationKey other)
    {
        if (!string.Equals(EntityTypeCode, other.EntityTypeCode, StringComparison.Ordinal))
        {
            return false;
        }
        if (!string.Equals(ProvenanceCode, other.ProvenanceCode, StringComparison.Ordinal))
        {
            return false;
        }
        if (ReferenceEquals(EntityHash, other.EntityHash))
        {
            return true;
        }
        if (EntityHash is null || other.EntityHash is null)
        {
            return false;
        }
        if (EntityHash.Length != other.EntityHash.Length)
        {
            return false;
        }
        return MemoryExtensions.SequenceEqual<byte>(EntityHash, other.EntityHash);
    }

    public override bool Equals(object? obj) => obj is EntityClassificationKey other && Equals(other);

    public override int GetHashCode()
    {
        int h = EntityHash is { Length: >= 4 } ? MemoryMarshal.Read<int>(EntityHash.AsSpan(0, 4)) : 0;
        return HashCode.Combine(h, EntityTypeCode, ProvenanceCode);
    }

    public static bool operator ==(EntityClassificationKey left, EntityClassificationKey right) => left.Equals(right);
    public static bool operator !=(EntityClassificationKey left, EntityClassificationKey right) => !left.Equals(right);
}
