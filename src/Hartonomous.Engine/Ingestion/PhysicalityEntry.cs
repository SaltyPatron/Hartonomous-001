using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One row queued for substrate.physicality. The pipeline writes
/// (physicality_type_id, entity_type_id, entity_hash, content_hash, geom)
/// directly — no resolve step. content_hash is computed at flush from the
/// WKB payload to deduplicate within (type, entity).
/// </summary>
internal readonly record struct PhysicalityEntry(
    EntityHandle Entity,
    string PhysicalityTypeCode,
    byte[] Wkb);
