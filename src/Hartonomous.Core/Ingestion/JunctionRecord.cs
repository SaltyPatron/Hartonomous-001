namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One row into one of the junction tables (entity_pos, entity_lexname,
/// entity_language, entity_morph_feature, model_architecture_class,
/// tensor_tensor_role, pattern_deprel). The sink validates JunctionTable
/// against the allowlist before COPY, then inserts into
/// substrate.staging_junction with table_name=JunctionTable as the
/// discriminator for the drain function.
///
/// Mu is non-null only for Glicko-bearing junctions (entity_pos, entity_sense,
/// pattern_deprel); the drain function COALESCEs to 1500.0 default for
/// missing values.
/// </summary>
public sealed record JunctionRecord(
    string JunctionTable,
    byte[] EntityHash,
    int ReferenceId,
    double? Mu = null) : IngestionRecord;
