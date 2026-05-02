namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.entity_model_source row. Lineage marker connecting an entity
/// (hash-only) to the model source it was derived from. ModelSourceId
/// resolves at the substrate side via reference table (substrate.model_source).
/// </summary>
public sealed record EntityModelSourceRecord(
    byte[] EntityHash,
    long ModelSourceId) : IngestionRecord;
