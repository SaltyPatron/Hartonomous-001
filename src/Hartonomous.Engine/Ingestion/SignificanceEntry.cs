using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One row for substrate.entity_significance. The pipeline writes
/// (context_type_id, entity_hash, attestation_type_id, mu, sigma, volatility, games)
/// directly. AttestationTypeCode null → defaults to
/// 'provenance_authority_corroboration' (ingestion-time priming).
/// </summary>
internal readonly record struct SignificanceEntry(
    EntityHandle Entity,
    string ContextTypeCode,
    double InitialMu,
    string? AttestationTypeCode = null);
