using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One row for substrate.entity_model_source. Entity → model_source FK pair.
/// model_source_id stays a BIGINT because model_source is a metadata table
/// with a SERIAL PK (it's not part of substrate identity).
/// </summary>
internal readonly record struct EntityModelSourceEntry(
    EntityHandle Entity,
    long ModelSourceId);
