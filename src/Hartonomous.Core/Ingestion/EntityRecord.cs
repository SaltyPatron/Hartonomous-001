namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.entity row plus its classification metadata.
///
/// Hash is the identity (BLAKE3 of content only — never placement metadata).
/// EntityTypeCode and ProvenanceCode are recorded on the
/// substrate.entity_classification junction (Phase C unification —
/// "dog is dog regardless of metadata"), not on substrate.entity itself.
/// The pipeline fans one EntityRecord into:
///   * substrate.staging_entity (hash only)
///   * substrate.staging_entity_classification (hash, type_id, provenance_id)
/// </summary>
public sealed record EntityRecord(
    string EntityTypeCode,
    byte[] Hash,
    string ProvenanceCode) : IngestionRecord;
