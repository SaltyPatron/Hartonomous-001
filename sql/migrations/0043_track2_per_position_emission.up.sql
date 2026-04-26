-- 0043_track2_per_position_emission.up.sql
--
-- Track-2 per-position substrate emission per
-- docs/specs/decomposers/safetensors.md § "Track 2 — Transformation weights
-- functionally sparsity-filtered" and architecture.md line 124
-- ("Knowledge extracted from neural network weights becomes explicit typed
-- edges in the substrate").
--
-- For each Track-2 SVD decomposition the existing SvdPass already emits a
-- per-tensor svd_spectrum entity (singular VALUES only) via has_spectrum.
-- The recomposer cannot synthesize tensor bytes from singular values alone —
-- it needs the per-rank singular VECTORS (U columns and V rows) AND their
-- positions in the original tensor's row-space and column-space.
--
-- This migration registers the per-position decomposition layer:
--
-- New entity type:
--   svd_rank_component — one per (tensor, rank) carrying σ_i, U_col_i, V_row_i
--                        as content. Hash is canonical (parent_tensor_hash +
--                        rank_index + sigma + U_bytes + V_bytes); the rank
--                        index IS content for this entity because two rank-3
--                        components from different tensors are different
--                        entities even if their (sigma, U, V) numerically
--                        coincide. Modality model_weights, routes to
--                        substrate.entity_default per migration 0006 layout
--                        (Track 1 per-token bpe_token entities go elsewhere;
--                        Track 2 per-rank components are derived analysis,
--                        analogous to the existing svd_spectrum etc.).
--
-- New edge type:
--   has_rank_component — tensor → svd_rank_component. Many edges per tensor
--                        (one per rank). Existing has_spectrum (tensor →
--                        svd_spectrum, summary statistics) coexists with
--                        these per-position edges; together they describe
--                        what the recomposer needs to scatter back into a
--                        target tensor: WHICH ranks (has_rank_component)
--                        and WHAT THEIR MAGNITUDES are (has_spectrum's
--                        svd_spectrum entity). Sourced from `tensor`
--                        following the established Track-2 convention in
--                        migration 0042.

INSERT INTO substrate.entity_type (code, modality) VALUES
    ('svd_rank_component', 'model_weights');

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_rank_component', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'svd_rank_component'));
