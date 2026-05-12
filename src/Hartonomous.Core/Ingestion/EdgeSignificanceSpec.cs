namespace Hartonomous.Core.Ingestion;

/// <summary>
/// Producer-supplied initial Glicko-2 mu for one (edge, arena, attestation_type)
/// triple, emitted alongside an edge by a decomposer that has computed a
/// calibrated prior from the source data (e.g. FfnEdgeDecompositionPass
/// derives a per-edge mu from the signed weight scaled by the tensor's mean
/// magnitude under attestation_type=model_ffn_full_path).
///
/// When present, the pipeline writes this mu to substrate.edge_significance
/// for the matching (arena, attestation_type) instead of the provenance.initial_mu
/// default. Arenas/attestation_types not covered by any spec receive defaults.
/// Producer overrides are inserted via the same EdgeSignificanceRecord channel
/// that the pipeline's auto-prime uses, with dedup keyed on
/// (arena, attestation_type, edge_type, hash) — so the override emits first
/// and the auto-prime's default is dropped.
/// </summary>
public readonly record struct EdgeSignificanceSpec(
    string ContextTypeCode,
    string AttestationTypeCode,
    double InitialMu);
