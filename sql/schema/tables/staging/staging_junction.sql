-- Single staging table for all junction-table writes. The 'table_name'
-- discriminator routes the drainer to entity_pos / entity_lexname /
-- entity_language / entity_morph_feature / model_architecture_class /
-- tensor_tensor_role / pattern_deprel / etc.; validated against the
-- substrate's junction allowlist by the drainer. mu is for the
-- Glicko-bearing junctions; null for the others.
CREATE TABLE IF NOT EXISTS substrate.staging_junction (
    table_name  TEXT  NOT NULL,
    entity_hash BYTEA NOT NULL,
    ref_id      INT   NOT NULL,
    mu          FLOAT8
);
COMMENT ON TABLE substrate.staging_junction IS
    'Persistent queue for junction-table writes. table_name routes to the right junction table; mu carries Glicko μ for Glicko-bearing junctions, null otherwise.';
