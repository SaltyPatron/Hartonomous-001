-- 0058_layer_norm_scale_per_role_unit.up.sql
--
-- Per-role unit emission for layer-norm-family transformation tensors
-- (LayerNorm, RmsNorm, BatchNorm). Supersedes OneDTensorPass for these
-- roles: OneDTensorPass attached the scale vector as a contour physicality
-- to the SOURCE TENSOR ENTITY, which means cross-model dedup of identical
-- scale vectors was impossible (the tensor entity is content+dtype+shape,
-- different models' tensors don't dedupe even with identical scale vectors).
--
-- This migration adds:
--
--   layer_norm_scale — one entity per LN/RMS/Batch-norm tensor's scale
--                      vector. Hashed by f64-canonical scale content only
--                      (no dtype/shape). Two layer norms across different
--                      models with identical scale vectors collapse to ONE
--                      entity → cross-model corroboration on layer-norm
--                      learned scales.
--
--   has_layer_norm_scale — tensor → layer_norm_scale. Layer index +
--                          norm position (pre_attn/post_attn/pre_ffn/
--                          post_ffn/final) recoverable from tensor's
--                          tensor_tensor_role junction and in_layer edge.
--
-- Sparsity is not applied here — every layer norm scale carries learned
-- meaning even when values are near 1.0; LN doesn't have "dead" scales
-- the way FFN has dead neurons. Full vector content is identity.

INSERT INTO substrate.entity_type (code, modality) VALUES
    ('layer_norm_scale', 'model_weights')
    ON CONFLICT (code) DO NOTHING;

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_layer_norm_scale', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'layer_norm_scale'))
    ON CONFLICT (code) DO NOTHING;
