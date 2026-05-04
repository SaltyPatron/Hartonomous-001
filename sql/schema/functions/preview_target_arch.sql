-- substrate.preview_target_arch(p_target_spec jsonb, p_recipe jsonb)
--
-- For a proposed target architecture spec + recipe, return per-tensor-role
-- counts of substrate edges that qualify under the recipe. Drives the
-- future model-config UI's "Preview" panel: estimated output size, sparsity
-- ratio, vocab coverage, expert clustering preview. NO files written.
--
-- p_target_spec example:
--   {"hidden_size":4096, "num_layers":32, "num_attention_heads":32,
--    "vocab_size":32768, "moe_experts":null, "ffn_intermediate":11008}
--
-- p_recipe example (Mode 2 origination, curated-only, semantic-relevance):
--   {"provenance_filter":"provenance.curator_class IN ('authoritative_standard','academic_curated')",
--    "arena_codes":["semantic_relevance","corroboration_strength"],
--    "significance_floor":0.7}
--
-- Returns one row per architectural-tensor role; the future UI aggregates
-- across roles to produce the headline estimate.
DROP FUNCTION IF EXISTS substrate.preview_target_arch(jsonb, jsonb);
CREATE OR REPLACE FUNCTION substrate.preview_target_arch(
    p_target_spec jsonb,
    p_recipe      jsonb
)
RETURNS TABLE (
    tensor_role               text,
    qualifying_edges          bigint,
    estimated_nonzero_count   bigint,
    sparsity_ratio            double precision,
    estimated_bytes           bigint
)
LANGUAGE plpgsql STABLE PARALLEL SAFE
AS $$
DECLARE
    v_hidden          int := COALESCE((p_target_spec->>'hidden_size')::int, 0);
    v_layers          int := COALESCE((p_target_spec->>'num_layers')::int, 0);
    v_heads           int := COALESCE((p_target_spec->>'num_attention_heads')::int, 0);
    v_vocab           int := COALESCE((p_target_spec->>'vocab_size')::int, 0);
    v_ffn_intermed    int := COALESCE((p_target_spec->>'ffn_intermediate')::int, v_hidden * 4);
    v_floor           double precision := COALESCE((p_recipe->>'significance_floor')::double precision, 0.5);
    v_arena_codes     text[];
    v_arena_ids       int[];
BEGIN
    -- Resolve arena codes → ids (open vocabulary; missing codes silently
    -- excluded so a recipe referencing a not-yet-created arena returns 0
    -- qualifying edges rather than error).
    IF p_recipe ? 'arena_codes' THEN
        SELECT array_agg(value)::text[] INTO v_arena_codes
          FROM jsonb_array_elements_text(p_recipe->'arena_codes');
    ELSE
        v_arena_codes := ARRAY['semantic_relevance', 'corroboration_strength'];
    END IF;

    SELECT array_agg(id) INTO v_arena_ids
      FROM substrate.significance_context
     WHERE code = ANY(v_arena_codes);

    -- For each architectural-tensor role, count substrate edges that
    -- qualify under the recipe (above significance floor in any of the
    -- requested arenas). The estimate count = qualifying_edges (= tensor
    -- count needed if we project one row per qualifying source unit);
    -- estimated_bytes scales by target dim and dtype.
    RETURN QUERY
    WITH role_buckets AS (
        SELECT 'attention_head_in_layer'::text AS role,
               v_layers::bigint * v_heads::bigint AS slot_count,
               (v_hidden::bigint * (v_hidden / GREATEST(v_heads, 1))::bigint) AS bytes_per_slot
        UNION ALL SELECT 'ffn_up_in_layer'::text,   v_layers::bigint, v_hidden::bigint * v_ffn_intermed::bigint
        UNION ALL SELECT 'ffn_gate_in_layer'::text, v_layers::bigint, v_hidden::bigint * v_ffn_intermed::bigint
        UNION ALL SELECT 'ffn_down_in_layer'::text, v_layers::bigint, v_ffn_intermed::bigint * v_hidden::bigint
        UNION ALL SELECT 'vocab_embedding'::text,   1::bigint,         v_vocab::bigint * v_hidden::bigint
        UNION ALL SELECT 'vocab_unembedding'::text, 1::bigint,         v_hidden::bigint * v_vocab::bigint
        UNION ALL SELECT 'layer_norm_for_layer_position'::text,
                                                    v_layers::bigint * 2::bigint, v_hidden::bigint
    ),
    edge_counts AS (
        SELECT et.code AS role,
               count(DISTINCT (es.edge_type_id, es.edge_hash)) FILTER (WHERE es.mu > v_floor) AS qualifying
          FROM substrate.edge_significance es
          JOIN substrate.edge_type et ON et.id = es.edge_type_id
         WHERE et.code IN (
                'attention_head_in_layer',
                'ffn_up_in_layer','ffn_gate_in_layer','ffn_down_in_layer',
                'vocab_embedding','vocab_unembedding',
                'layer_norm_for_layer_position'
           )
           AND (v_arena_ids IS NULL OR es.context_type_id = ANY(v_arena_ids))
         GROUP BY et.code
    )
    SELECT rb.role,
           COALESCE(ec.qualifying, 0)::bigint                           AS qualifying_edges,
           LEAST(COALESCE(ec.qualifying, 0), rb.slot_count)::bigint     AS estimated_nonzero_count,
           CASE
              WHEN rb.slot_count = 0 THEN 0.0
              ELSE 1.0 - (LEAST(COALESCE(ec.qualifying, 0), rb.slot_count)::double precision
                          / rb.slot_count::double precision)
           END                                                          AS sparsity_ratio,
           (rb.slot_count * rb.bytes_per_slot * 2)::bigint              AS estimated_bytes  -- BF16 = 2 bytes/element
      FROM role_buckets rb
      LEFT JOIN edge_counts ec ON ec.role = rb.role
     ORDER BY rb.role;
END $$;

COMMENT ON FUNCTION substrate.preview_target_arch(jsonb, jsonb) IS
    'Per-tensor-role preview for a proposed target architecture + recipe. Returns qualifying edge counts, estimated nonzero counts, sparsity ratio, byte estimates. NO files written. Drives the future model-config UI''s preview panel.';
