using System;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// PK shape for <c>substrate.entity_classification</c> at the producer side
/// where reference codes haven't yet been resolved to IDs. Decomposers
/// build sets of these from precomputed (hash, type, provenance) candidates
/// and pass them to <c>IIngestionPipeline.GetExistingEntityClassificationsAsync</c>.
/// </summary>
public readonly struct EntityClassificationKey : IEquatable<EntityClassificationKey>
{
    public Hash32 EntityHash { get; }
    public string EntityTypeCode { get; }
    public string ProvenanceCode { get; }

    public EntityClassificationKey(Hash32 entityHash, string entityTypeCode, string provenanceCode)
    {
        ArgumentNullException.ThrowIfNull(entityTypeCode);
        ArgumentNullException.ThrowIfNull(provenanceCode);
        EntityHash = entityHash;
        EntityTypeCode = entityTypeCode;
        ProvenanceCode = provenanceCode;
    }

    public EntityClassificationKey(byte[] entityHash, string entityTypeCode, string provenanceCode)
        : this(new Hash32(entityHash), entityTypeCode, provenanceCode)
    {
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
        return EntityHash.Equals(other.EntityHash);
    }

    public override bool Equals(object? obj) => obj is EntityClassificationKey other && Equals(other);

    public override int GetHashCode()
    {
        return HashCode.Combine(EntityHash, EntityTypeCode, ProvenanceCode);
    }

    public static bool operator ==(EntityClassificationKey left, EntityClassificationKey right) => left.Equals(right);
    public static bool operator !=(EntityClassificationKey left, EntityClassificationKey right) => !left.Equals(right);
}
