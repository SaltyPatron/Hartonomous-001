using Hartonomous.Core.Geometry;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One row queued for substrate.physicality. The pipeline writes
/// (physicality_type_id, entity_hash, content_hash, geom) directly — no
/// resolve step. content_hash is computed at flush from the geometry payload to
/// deduplicate within (type, entity).
///
/// <para>
/// <see cref="Centroid"/> is the entity's representative 4D point — the
/// value the pipeline uses to construct inline LINESTRING4D trajectories at
/// edge-emit time. For POINT4D physicality it equals the point itself; for
/// LINESTRING4D and other composite shapes it is the unweighted 4D mean of
/// the geometry payload's vertex stream. The decomposer that emits the physicality knows
/// this value (it had to compute it to assemble the geometry), so we carry it
/// alongside the payload instead of forcing the pipeline to re-parse geometry or
/// to round-trip to substrate.physicality at edge-build time.
/// </para>
/// </summary>
internal readonly record struct PhysicalityEntry(
    EntityHandle Entity,
    string PhysicalityTypeCode,
    byte[] Geometry,
    Point4D Centroid,
    Hash32[]? ChildHashes = null,
    int[]? OrdinalStarts = null,
    int[]? RleCounts = null);
