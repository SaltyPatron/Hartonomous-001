using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.edge row. EdgeHash = BLAKE3(edge_type_id || ordered
/// participant hashes), computed by the decomposer and passed in. Provenance
/// trust prior is resolved by code at sink time.
///
/// Edge members are emitted as separate <see cref="EdgeMemberRecord"/> values
/// so the sink can write edge and edge_member to different drain queues in
/// parallel. Decomposers emit the EdgeRecord first, then all its
/// EdgeMemberRecord values — substrate-side composite-PK enforcement on
/// edge_member's INSERT-SELECT references the edge row inserted by the same
/// chunk (within-session) or a prior chunk (cross-session, ON CONFLICT
/// handled).
///
/// <see cref="Geometry"/> carries the pre-built LINESTRINGZM payload for
/// the edge's trajectory (participants' mantissa-packed identity-POINTZMs
/// in role order). The bundled-emit pipeline builds it inline at edge-emit
/// from the bundle's edge_members; the drain INSERT writes it straight
/// into substrate.edge.geom. There is no NULL-geom window, no end-of-phase
/// backfill (AP-37).
/// </summary>
public sealed record EdgeRecord(
    string EdgeTypeCode,
    Hash32 EdgeHash,
    string ProvenanceCode,
    byte[]? Geometry = null) : IngestionRecord;
