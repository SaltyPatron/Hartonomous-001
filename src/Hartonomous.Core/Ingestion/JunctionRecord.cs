namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One row into one of the junction tables (entity_pos, entity_lexname,
/// entity_language, entity_morph_feature, model_architecture_class,
/// tensor_tensor_role, pattern_deprel). The sink validates JunctionTable
/// against the allowlist before COPY, then inserts into
/// a per-connection temporary inflight table before set-based insert into
/// the selected junction table.
///
/// Mu is non-null only for Glicko-bearing junctions (entity_pos and
/// pattern_deprel); the drain path COALESCEs to the default for missing
/// values.
/// </summary>
public sealed record JunctionRecord(
    string JunctionTable,
    byte[] EntityHash,
    int ReferenceId,
    double? Mu = null) : IngestionRecord;
