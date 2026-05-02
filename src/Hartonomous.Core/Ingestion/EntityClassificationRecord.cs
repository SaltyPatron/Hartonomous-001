namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.entity_classification row. Records that a particular
/// decomposer (provenance) asserts a particular content (entity_hash) bears
/// a particular classification (entity_type_id). Multiple decomposers may
/// independently assert classifications on the same hash; provenance keeps
/// them distinct without fragmenting identity.
///
/// Normally produced automatically when an EntityRecord is emitted; can be
/// emitted directly by decomposers that want to assert additional
/// classifications on a pre-existing hash.
/// </summary>
public sealed record EntityClassificationRecord(
    byte[] EntityHash,
    string EntityTypeCode,
    string ProvenanceCode) : IngestionRecord;
