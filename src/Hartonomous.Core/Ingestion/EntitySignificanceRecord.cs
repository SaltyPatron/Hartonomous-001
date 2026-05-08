namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.entity_significance row, stratified by attestation_type.
/// Same (arena, entity) carries separate ratings under different
/// attestation_types — corpus_co_occurrence_window vs lexical_curated_relation
/// vs model_attention_pattern vs inference_outcome_accept etc. — so
/// kinds-of-evidence remain distinguishable. Initial Mu seeded by the
/// decomposer's per-context trust prior; sigma/volatility/games default at
/// the substrate side (350.0 / 0.06 / 0). Decomposer-emitted significance is
/// the at-ingest seed; arena outcomes update via Glicko-2 later.
/// </summary>
public sealed record EntitySignificanceRecord(
    string ContextTypeCode,
    string AttestationTypeCode,
    byte[] EntityHash,
    double InitialMu) : IngestionRecord;
