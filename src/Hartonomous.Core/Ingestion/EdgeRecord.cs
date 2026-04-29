namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.edge row. EdgeHash = BLAKE3(edge_type_id || ordered
/// participant hashes), computed by the decomposer and passed in. Provenance
/// trust prior is resolved by code at sink time.
///
/// Edge members are emitted as separate <see cref="EdgeMemberRecord"/> values
/// so the sink can COPY edge and edge_member into different staging tables
/// in parallel without coupling them in one record shape. Decomposer is
/// responsible for emitting the EdgeRecord first, then all its EdgeMemberRecord
/// values — the substrate's composite-FK enforcement happens at flush time
/// (drain_staging_edge_member runs after drain_staging_edge in the worker).
/// </summary>
public sealed record EdgeRecord(
    string EdgeTypeCode,
    byte[] EdgeHash,
    string ProvenanceCode) : IngestionRecord;
