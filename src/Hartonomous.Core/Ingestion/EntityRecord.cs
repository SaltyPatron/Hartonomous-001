namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.entity row. Sink COPYs (entity_type_id, hash) into
/// substrate.staging_entity; the background flush worker drains
/// staging→substrate.entity per-partition.
///
/// Hash is the BLAKE3 of content only (never placement metadata —
/// <see cref="P:Hartonomous.Core.Decomposition.BaseDecomposer.ComputeHash"/>).
/// </summary>
public sealed record EntityRecord(
    string EntityTypeCode,
    byte[] Hash) : IngestionRecord;
