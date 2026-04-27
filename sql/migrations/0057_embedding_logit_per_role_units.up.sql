-- 0057_embedding_logit_per_role_units.up.sql
--
-- Adds per-role unit entity types and edges for two more critical
-- transformation tensor roles per the corrected build plan P5:
--
--   embedding_position — one entity per row of a TOKEN_EMBEDDING tensor.
--                        Hashed by f64-canonical row content. Same row
--                        content across models collapses to ONE entity.
--                        The recomposer scatters these into a target
--                        embedding tensor at distillation. Each row's
--                        firefly (4D Laplacian-eigenmap projection) is a
--                        separate physicality on the bpe_token entity per
--                        EmbeddingFireflyPass; this pass stores the FULL
--                        ROW so the recomposer can reproduce embedding
--                        bytes losslessly at distillation.
--
--   logit_projection   — one entity per row of a LOGIT_HEAD tensor (the
--                        per-vocab-token output projection direction).
--                        Hashed by f64-canonical row content. Cross-model
--                        corroboration on logit projections is the same
--                        substrate-level mechanism as for FFN neurons.
--
-- Edges:
--   has_embedding_position — tensor → embedding_position
--   has_logit_projection   — tensor → logit_projection
--
-- Placement (which row index) is recorded via substrate.sequence with
-- ordinal_position = row_index. Layer/projection role is recoverable from
-- the source tensor's tensor_tensor_role junction.

INSERT INTO substrate.entity_type (code, modality) VALUES
    ('embedding_position', 'model_weights'),
    ('logit_projection',   'model_weights')
    ON CONFLICT (code) DO NOTHING;

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_embedding_position', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'embedding_position')),
    ('has_logit_projection', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'logit_projection'))
    ON CONFLICT (code) DO NOTHING;
