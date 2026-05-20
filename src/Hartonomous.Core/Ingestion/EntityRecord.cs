using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.entity row plus its classification metadata.
///
/// Hash is the identity (BLAKE3 of content only — never placement metadata).
/// EntityTypeCode and ProvenanceCode are recorded on the
/// substrate.entity_classification junction. The pipeline fans one
/// EntityRecord into:
///   * substrate.entity (hash only — identity)
///   * substrate.entity_classification (hash, type_id, provenance_id)
///
/// Geometry lives on substrate.physicality and is carried by
/// <see cref="PhysicalityRecord"/>, not here. substrate.entity is identity only.
/// </summary>
public sealed record EntityRecord(
    string EntityTypeCode,
    Hash32 Hash,
    string ProvenanceCode) : IngestionRecord;
