using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Engine;

/// <summary>
/// Metadata about an entity in a traversal path. Carries the composite
/// handle (type code + hash) and an optional human-readable content label.
///
/// Hash-as-PK: <see cref="Handle"/> is the composite primary key; there is
/// no surrogate id field.
/// </summary>
public sealed record EntityInfo
{
    /// <summary>Composite identity: entity type code + BLAKE3 content hash.</summary>
    public required EntityHandle Handle { get; init; }

    /// <summary>
    /// Human-readable content label when available (e.g., the word form text,
    /// synset gloss summary). Null for binary-only entities.
    /// </summary>
    public string? ContentLabel { get; init; }

    /// <summary>Convenience accessor for the entity type code.</summary>
    public string EntityTypeCode => Handle.EntityTypeCode;

    /// <summary>Convenience accessor for the BLAKE3 content hash.</summary>
    public Hash32 Hash => Handle.Hash;
}
