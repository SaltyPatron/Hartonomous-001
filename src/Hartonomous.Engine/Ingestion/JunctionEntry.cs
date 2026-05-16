using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One row for a junction table. The pipeline routes by junction-table name
/// and writes (entity_hash, reference_id, attestation_type_id [, mu]) directly.
/// AttestationTypeCode is required for Glicko-bearing junctions (entity_pos,
/// pattern_deprel) — null defaults to 'positive_evidence' downstream.
/// </summary>
internal readonly record struct JunctionEntry(
    string JunctionTable,
    EntityHandle Entity,
    int ReferenceId,
    double? Mu,
    string? AttestationTypeCode = null);
