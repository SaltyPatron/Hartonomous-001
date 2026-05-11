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
/// <see cref="GeomWkb"/> carries an optional pre-built LINESTRINGZM EWKB
/// for the edge's trajectory (participant centroids in role order). When
/// non-null, the drain INSERT lifts it via ST_GeomFromWKB straight into
/// substrate.edge.geom — no post-pass populate. When null, the row goes in
/// with geom = NULL and an end-of-phase
/// substrate.populate_edge_trajectories populates from
/// substrate.edge_member ⋈ substrate.physicality (s3_position).
/// </summary>
public sealed record EdgeRecord(
    string EdgeTypeCode,
    Hash32 EdgeHash,
    string ProvenanceCode,
    byte[]? GeomWkb = null) : IngestionRecord;
