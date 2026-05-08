namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.edge_significance row, stratified by attestation_type.
/// Same (arena, edge) carries separate ratings per attestation_type — corpus
/// co-occurrence events, model attention attestations, lexicon-curated
/// relations, and inference outcomes accumulate as distinct rating rows so
/// the recomposer's WHERE clause and the inference engine's blend can pull
/// circuit-only-students, lexicon-only-students, etc. without losing
/// per-evidence-kind detail.
///
/// Initial Mu seeded by the decomposer's per-context trust prior;
/// sigma/volatility/games default at the substrate side (350.0 / 0.06 / 0).
///
/// Edge significance is partitioned by context_type_id (one partition per
/// arena). The producer cross-products every emitted edge against every
/// arena currently in substrate.significance_context (per AP-1: no arena
/// cherry-picking).
/// </summary>
public sealed record EdgeSignificanceRecord(
    string ContextTypeCode,
    string AttestationTypeCode,
    string EdgeTypeCode,
    byte[] EdgeHash,
    double InitialMu) : IngestionRecord;
