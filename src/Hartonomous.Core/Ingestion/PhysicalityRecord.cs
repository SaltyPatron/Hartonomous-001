using Hartonomous.Core.Geometry;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.physicality row. <see cref="Geometry"/> is the native
/// geometry4d payload for the substrate-side geometry value.
///
/// Content hash is BLAKE3 of the geometry payload; uniqueness inside
/// substrate.physicality is (physicality_type_id, entity_hash, content_hash)
/// so the same geometry contributed by multiple passes deduplicates.
///
/// <see cref="Centroid"/> is the entity's representative 4D point — the
/// unweighted 4D mean of the payload's vertex stream (or the point itself for
/// POINT4D). It is carried alongside the payload so the pipeline can build
/// inline LINESTRING4D trajectories for edges at submit time without
/// round-tripping to substrate.physicality.
/// </summary>
public sealed record PhysicalityRecord(
    string PhysicalityTypeCode,
    Hash32 EntityHash,
    Hash32 ContentHash,
    byte[] Geometry,
    Point4D Centroid,
    Hash32[]? ChildHashes = null,
    int[]? OrdinalStarts = null,
    int[]? RleCounts = null) : IngestionRecord;
