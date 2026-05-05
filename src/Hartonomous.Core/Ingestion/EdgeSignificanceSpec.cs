namespace Hartonomous.Core.Ingestion;

/// <summary>
/// Producer-supplied initial Glicko-2 mu for one (edge, arena) pair, emitted
/// alongside an edge by a decomposer that has computed a calibrated prior
/// from the source data (e.g. FfnEdgeDecompositionPass derives a per-edge
/// mu from the signed weight scaled by the tensor's mean magnitude).
///
/// When present, the pipeline writes this mu to substrate.edge_significance
/// for the matching arena instead of the provenance.initial_mu default.
/// Arenas not covered by any spec receive the default. Producer overrides
/// are inserted via the same EdgeSignificanceRecord channel that the
/// pipeline's auto-prime uses, with dedup keyed on (arena, edge_type, hash)
/// — so the override emits first and the auto-prime's default is dropped.
/// </summary>
public readonly record struct EdgeSignificanceSpec(
    string ContextTypeCode,
    double InitialMu);
