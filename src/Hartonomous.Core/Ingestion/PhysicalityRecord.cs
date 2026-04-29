namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.physicality row. Wkb is the binary WKB encoding of the
/// 4D geometry (POINTZM for atoms, LINESTRINGZM for compositions, etc.) —
/// see PostGisWkbBuilder. The drain function calls ST_GeomFromWKB to
/// rebuild the geometry on the substrate side.
///
/// Content hash is BLAKE3 of the WKB bytes; uniqueness inside
/// substrate.physicality is (physicality_type_id, entity_type_id,
/// entity_hash, content_hash) so the same geometry contributed by multiple
/// passes deduplicates rather than duplicating rows.
/// </summary>
public sealed record PhysicalityRecord(
    string PhysicalityTypeCode,
    string EntityTypeCode,
    byte[] EntityHash,
    byte[] ContentHash,
    byte[] Wkb) : IngestionRecord;
