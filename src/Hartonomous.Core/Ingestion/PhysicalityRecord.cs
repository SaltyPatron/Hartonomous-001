using Hartonomous.Core.Geometry;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.physicality row. <see cref="Geometry"/> is the native
/// geometry4d payload that becomes substrate.physicality.geom.
///
/// <para>
/// For atom physicalities (POINTZM): the payload is a real-coord POINTZM at
/// the atom's content-derived centroid (codepoint Super-Fibonacci S^3, audio
/// frame, image pixel, etc.).
/// </para>
///
/// <para>
/// For composition physicalities (LINESTRINGZM / MULTILINESTRINGZM): the
/// payload's vertices are mantissa-packed child refs per the substrate
/// mantissa packing contract — each vertex is
/// <c>(bb_pack_hash_lo(child_hash_bits_0_51), bb_pack_ordinal_rle(ordinal, rle),
/// bb_pack_hash_hi(child_hash_bits_52_103), bb_pack_metadata(0))</c>.
/// The geometry IS the indexed relational child manifest; no sidecar arrays.
/// </para>
///
/// Content hash is BLAKE3 of the geometry payload; uniqueness inside
/// substrate.physicality is (physicality_type_id, entity_hash, content_hash)
/// so the same geometry contributed by multiple passes deduplicates.
/// </summary>
public sealed record PhysicalityRecord(
    string PhysicalityTypeCode,
    Hash32 EntityHash,
    Hash32 ContentHash,
    byte[] Geometry) : IngestionRecord;
