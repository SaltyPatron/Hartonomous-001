-- substrate.consensus_token_pairs(
--     p_arena_code      TEXT,
--     p_attestation_codes TEXT[]   DEFAULT NULL,
--     p_min_mu          FLOAT8   DEFAULT 1500.0,
--     p_min_attestations INT     DEFAULT 2,
--     p_limit           INT      DEFAULT 1000
-- )
--
-- Returns token↔token edges where the substrate has consensus across
-- multiple model decompositions. "Consensus" = at least p_min_attestations
-- distinct attestation events on the edge in the requested arena (counted
-- by the games column on edge_significance), filtered by attestation_type
-- if p_attestation_codes is set, mu above p_min_mu.
--
-- Use case: after decomposing Llama4-Maverick + Qwen3-480B (or any N
-- models), this function surfaces the edges where the models AGREE about
-- token-pair relationships. Edges with games=1 had only one model attest
-- to them; edges with games >= N indicate cross-model corroboration. The
-- recomposer's WHERE-clause distillation pulls from this consensus when
-- producing a new student model that reflects shared knowledge.
--
-- Returns one row per qualifying edge: token_a (sorted lower hash for
-- symmetric edges, source for directed), token_b, blended_mu, attestation
-- count, list of attestation_types present.

CREATE OR REPLACE FUNCTION substrate.consensus_token_pairs(
    p_arena_code        TEXT,
    p_attestation_codes TEXT[] DEFAULT NULL,
    p_min_mu            FLOAT8 DEFAULT 1500.0,
    p_min_attestations  INT    DEFAULT 2,
    p_limit             INT    DEFAULT 1000
)
RETURNS TABLE (
    edge_type_code        TEXT,
    edge_hash             BYTEA,
    token_a_hash          BYTEA,
    token_b_hash          BYTEA,
    blended_mu            FLOAT8,
    total_games           INT,
    attestation_types     TEXT[]
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH arena AS (
        SELECT id FROM substrate.significance_context WHERE code = p_arena_code
    ),
    qualifying_significance AS (
        SELECT
            es.edge_type_id,
            es.edge_hash,
            es.mu,
            es.games,
            at.code AS attestation_code
          FROM substrate.edge_significance es
          JOIN substrate.attestation_type at ON at.id = es.attestation_type_id
         WHERE es.context_type_id = (SELECT id FROM arena)
           AND es.mu >= p_min_mu
           AND (p_attestation_codes IS NULL OR at.code = ANY(p_attestation_codes))
    ),
    aggregated AS (
        SELECT
            qs.edge_type_id,
            qs.edge_hash,
            AVG(qs.mu) AS blended_mu,
            SUM(qs.games)::INT AS total_games,
            array_agg(qs.attestation_code ORDER BY qs.attestation_code) AS attestation_types
          FROM qualifying_significance qs
         GROUP BY qs.edge_type_id, qs.edge_hash
        HAVING SUM(qs.games) >= p_min_attestations
    ),
    with_members AS (
        SELECT
            et.code AS edge_type_code,
            a.edge_hash,
            a.blended_mu,
            a.total_games,
            a.attestation_types,
            (
                SELECT em.entity_hash
                  FROM substrate.edge_member em
                  JOIN substrate.edge_role er ON er.id = em.edge_role_id
                 WHERE em.edge_type_id = a.edge_type_id
                   AND em.edge_hash    = a.edge_hash
                   AND er.code         = 'source'
                 LIMIT 1
            ) AS token_a_hash,
            (
                SELECT em.entity_hash
                  FROM substrate.edge_member em
                  JOIN substrate.edge_role er ON er.id = em.edge_role_id
                 WHERE em.edge_type_id = a.edge_type_id
                   AND em.edge_hash    = a.edge_hash
                   AND er.code         = 'target'
                 LIMIT 1
            ) AS token_b_hash
          FROM aggregated a
          JOIN substrate.edge_type et ON et.id = a.edge_type_id
         WHERE et.code IN ('model_concept_similarity', 'model_attention_pattern', 'model_ffn_factor', 'co_occurrence')
    )
    SELECT
        edge_type_code,
        edge_hash,
        token_a_hash,
        token_b_hash,
        blended_mu,
        total_games,
        attestation_types
      FROM with_members
     WHERE token_a_hash IS NOT NULL AND token_b_hash IS NOT NULL
     ORDER BY blended_mu DESC, total_games DESC
     LIMIT p_limit;
$$;

COMMENT ON FUNCTION substrate.consensus_token_pairs(TEXT, TEXT[], FLOAT8, INT, INT) IS
    'Surface token-pair edges with cross-model consensus. Filters by arena, attestation_types, mu floor, and minimum attestation count. Returns blended mu (avg across attestation_types), total games, and the full attestation_type set present. Used by the recomposer''s WHERE-clause distillation to identify the substrate''s accumulated cross-model agreement.';
