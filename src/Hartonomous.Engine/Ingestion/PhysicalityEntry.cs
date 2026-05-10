using Hartonomous.Core.Geometry;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One row queued for substrate.physicality. The pipeline writes
/// (physicality_type_id, entity_hash, content_hash, geom) directly — no
/// resolve step. content_hash is computed at flush from the WKB payload to
/// deduplicate within (type, entity).
///
/// <para>
/// <see cref="Centroid"/> is the entity's representative 4D point — the
/// value the pipeline uses to construct inline LINESTRINGZM trajectories at
/// edge-emit time. For POINTZM physicality it equals the point itself; for
/// LINESTRINGZM and other composite shapes it is the unweighted 4D mean of
/// the WKB's vertex stream. The decomposer that emits the physicality knows
/// this value (it had to compute it to assemble the WKB), so we carry it
/// alongside the WKB instead of forcing the pipeline to re-parse the WKB or
/// to round-trip to substrate.physicality at edge-build time.
/// </para>
/// </summary>
internal readonly record struct PhysicalityEntry(
    EntityHandle Entity,
    string PhysicalityTypeCode,
    byte[] Wkb,
    Point4D Centroid);
