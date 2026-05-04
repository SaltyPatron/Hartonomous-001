namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.edge_significance row. Initial Mu seeded by the decomposer's
/// per-context trust prior; sigma/volatility/games default at the substrate
/// side (350.0 / 0.06 / 0). Decomposer-emitted edge significance is the
/// at-ingest seed; arena outcomes update via Glicko-2 later.
///
/// Edge significance is partitioned by context_type_id (one partition per
/// arena). The producer cross-products every emitted edge against every
/// arena currently in substrate.significance_context (per AP-1: no arena
/// cherry-picking). Replaces the deleted BackgroundSignificancePrimer's
/// substrate.prime_unprimed_edges_chunk pumping.
/// </summary>
public sealed record EdgeSignificanceRecord(
    string ContextTypeCode,
    string EdgeTypeCode,
    byte[] EdgeHash,
    double InitialMu) : IngestionRecord;
