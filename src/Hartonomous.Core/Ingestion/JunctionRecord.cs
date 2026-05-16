using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One row into one of the junction tables (entity_pos, entity_lexname,
/// entity_language, entity_morph_feature, model_architecture_class,
/// tensor_tensor_role, pattern_deprel). The sink validates JunctionTable
/// against the allowlist before COPY, then inserts into a per-connection
/// temporary inflight table before set-based insert into the selected
/// junction table.
///
/// AttestationTypeCode is required for Glicko-bearing junctions (entity_pos,
/// pattern_deprel) — the new PK column stratifies the rating per kind of
/// evidence (lexical curators vs model attention patterns vs corpus
/// statistics). For non-Glicko junctions the field is ignored at the drain
/// boundary; pass any valid code (e.g. positive_evidence).
///
/// Mu is non-null only for Glicko-bearing junctions; the drain path
/// COALESCEs to the default for missing values.
/// </summary>
public sealed record JunctionRecord(
    string JunctionTable,
    Hash32 EntityHash,
    int ReferenceId,
    string AttestationTypeCode,
    double? Mu = null) : IngestionRecord;
