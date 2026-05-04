-- substrate.recompose_audit_walk(p_provenance_chain jsonb)
--
-- Walks a recomposed model's __metadata__.hartonomous_provenance_chain
-- back through the substrate to verify every claimed (tensor, source,
-- arena, μ) tuple actually exists in current substrate state. Returns
-- one row per chain entry with verified=true/false and a divergence
-- detail string. The D-recompose-audit-chain gate runs this for every
-- exported tensor.
--
-- p_provenance_chain example (one entry per output tensor):
--   [
--     {"tensor_hash":"<hex>","provenance":"huggingface_model:llama-4-maverick","arena":"corroboration_strength","mu":78321.5},
--     ...
--   ]
--
-- Implementation: one flat SELECT, no CTE, no plpgsql.
--   * jsonb_array_elements WITH ORDINALITY (native built-in) expands the
--     chain to rows preserving original order.
--   * jsonb_to_record (native C) extracts named fields per row.
--   * LATERAL JOIN with LIMIT 1 (executor-level, native) does one indexed
--     lookup per chain row against substrate.edge_significance.
DROP FUNCTION IF EXISTS substrate.recompose_audit_walk(jsonb);
CREATE OR REPLACE FUNCTION substrate.recompose_audit_walk(p_provenance_chain jsonb)
RETURNS TABLE (
    chain_index int,
    tensor_hash bytea,
    claimed_mu  double precision,
    actual_mu   double precision,
    verified    boolean,
    detail      text
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT
        arr.ordinality::int                                                AS chain_index,
        decode(j.tensor_hash, 'hex')                                       AS tensor_hash,
        j.mu                                                                AS claimed_mu,
        actual.mu                                                           AS actual_mu,
        actual.mu IS NOT NULL
            AND abs(COALESCE(actual.mu, 0) - COALESCE(j.mu, 0)) < 1.0       AS verified,
        CASE WHEN actual.mu IS NULL THEN 'no edge in current substrate'
             WHEN abs(actual.mu - j.mu) >= 1.0 THEN
                 format('mu drift: claimed=%s actual=%s', j.mu, actual.mu)
             ELSE 'ok' END                                                  AS detail
      FROM jsonb_array_elements(p_provenance_chain) WITH ORDINALITY
        AS arr(elem, ordinality)
      CROSS JOIN LATERAL jsonb_to_record(arr.elem)
        AS j(tensor_hash text, provenance text, arena text, mu double precision)
      LEFT JOIN LATERAL (
          SELECT es.mu
            FROM substrate.edge_member em
            JOIN substrate.edge e
              ON e.edge_type_id = em.edge_type_id
             AND e.hash         = em.edge_hash
            JOIN substrate.provenance prov
              ON prov.id   = e.provenance_id
             AND prov.code = j.provenance
            JOIN substrate.edge_significance es
              ON es.edge_type_id = e.edge_type_id
             AND es.edge_hash    = e.hash
            JOIN substrate.significance_context sc
              ON sc.id   = es.context_type_id
             AND sc.code = j.arena
           WHERE em.entity_hash = decode(j.tensor_hash, 'hex')
           ORDER BY es.mu DESC NULLS LAST
           LIMIT 1
      ) actual ON TRUE
     ORDER BY arr.ordinality;
$$;

COMMENT ON FUNCTION substrate.recompose_audit_walk(jsonb) IS
    'Verify every (tensor, provenance, arena, μ) entry in a recomposed model''s __metadata__ provenance chain. Flat SELECT — jsonb_array_elements WITH ORDINALITY + jsonb_to_record (native C) + LATERAL LIMIT 1 (native executor). No CTE, no plpgsql.';
