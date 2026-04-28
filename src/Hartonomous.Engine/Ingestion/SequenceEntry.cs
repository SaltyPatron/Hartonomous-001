using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One row queued for substrate.sequence. The pipeline writes
/// (parent_entity_type_id, parent_entity_hash, ordinal, child_entity_type_id,
/// child_entity_hash, rle_count) with composite hash addressing throughout.
/// </summary>
internal readonly record struct SequenceEntry(
    EntityHandle Parent,
    int Ordinal,
    EntityHandle Child,
    int RleCount);
