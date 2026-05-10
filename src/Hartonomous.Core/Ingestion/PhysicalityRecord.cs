using Hartonomous.Core.Geometry;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.physicality row. <see cref="Wkb"/> is the binary WKB
/// encoding of the 4D geometry (POINTZM for atoms, LINESTRINGZM for
/// compositions, etc.) — see PostGisWkbBuilder. The drain function calls
/// ST_GeomFromWKB to rebuild the geometry on the substrate side.
///
/// Content hash is BLAKE3 of the WKB bytes; uniqueness inside
/// substrate.physicality is (physicality_type_id, entity_hash, content_hash)
/// so the same geometry contributed by multiple passes deduplicates.
///
/// <see cref="Centroid"/> is the entity's representative 4D point — the
/// unweighted 4D mean of the WKB's vertex stream (or the point itself for
/// POINTZM). It is carried alongside the WKB so the pipeline can build
/// inline LINESTRINGZM trajectories for edges at submit time without
/// re-parsing the WKB or round-tripping to substrate.physicality.
/// </summary>
public sealed record PhysicalityRecord(
    string PhysicalityTypeCode,
    byte[] EntityHash,
    byte[] ContentHash,
    byte[] Wkb,
    Point4D Centroid) : IngestionRecord;
