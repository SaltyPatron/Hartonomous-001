-- substrate.refinement_summary(p_model_arch_hash bytea, p_arena_code text DEFAULT 'corroboration_strength')
--
-- Per-tensor refinement preview for an ingested model. For each tensor with
-- an architectural edge, reports:
--   source_only_mu  — edge significance using only the source model's
--                     sub-provenance contribution (μ at provenance-default).
--   consensus_mu    — edge significance with cross-source corroboration in
--                     the requested arena (μ that would be used if
--                     RefinementPolicy = Consensus).
--   delta_mu        — consensus_mu - source_only_mu (positive = corroborated,
--                     pushed up; negative = contradicted, pushed down).
--   above_threshold — whether the consensus μ clears a typical 0.7 floor.
--
-- The recomposer can be queried with this function to preview which
-- positions will be reinforced vs which will be zeroed out at recompose.
-- The future UI plots delta_mu as a histogram so the user can see how
-- much the substrate's accumulated cross-source state will reshape this
-- model on refined export.
DROP FUNCTION IF EXISTS substrate.refinement_summary(bytea, text);
CREATE OR REPLACE FUNCTION substrate.refinement_summary(
    p_model_arch_hash bytea,
    p_arena_code      text DEFAULT 'corroboration_strength'
)
RETURNS TABLE (
    tensor_hash          bytea,
    edge_type_code       text,
    source_only_mu       double precision,
    consensus_mu         double precision,
    delta_mu             double precision,
    above_threshold      boolean
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH arena AS (
        SELECT id FROM substrate.significance_context WHERE code = p_arena_code
    ),
    model_tensors AS (
        SELECT em_src.entity_hash AS tensor_hash, et.code AS edge_type_code,
               em_src.edge_type_id, em_src.edge_hash
          FROM substrate.edge_member em_tgt
          JOIN substrate.edge_type et      ON et.id = em_tgt.edge_type_id
          JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
          JOIN substrate.edge_member em_src
            ON em_src.edge_type_id = em_tgt.edge_type_id
           AND em_src.edge_hash    = em_tgt.edge_hash
          JOIN substrate.edge_role er_src ON er_src.id = em_src.edge_role_id AND er_src.code = 'source'
         WHERE em_tgt.entity_hash = p_model_arch_hash
           AND et.category = 'model_derived'
    )
    SELECT mt.tensor_hash,
           mt.edge_type_code,
           p.initial_mu * et.semantic_weight * p.derivation_decay AS source_only_mu,
           es.mu AS consensus_mu,
           es.mu - (p.initial_mu * et.semantic_weight * p.derivation_decay) AS delta_mu,
           es.mu > 0.7 * p.initial_mu AS above_threshold
      FROM model_tensors mt
      JOIN substrate.edge e         ON e.edge_type_id = mt.edge_type_id AND e.hash = mt.edge_hash
      JOIN substrate.edge_type et   ON et.id = e.edge_type_id
      JOIN substrate.provenance p   ON p.id = e.provenance_id
      JOIN arena                    ON TRUE
      JOIN substrate.edge_significance es
        ON es.edge_type_id = e.edge_type_id
       AND es.edge_hash    = e.hash
       AND es.context_type_id = arena.id
     ORDER BY delta_mu DESC NULLS LAST;
$$;

COMMENT ON FUNCTION substrate.refinement_summary(bytea, text) IS
    'Per-tensor refinement preview: source-only μ vs cross-source-consensus μ vs threshold. Identifies positions that will be reinforced or zeroed at recompose. The future UI plots this as a histogram.';
