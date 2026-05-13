-- substrate.complete(seed_hash, max_depth, max_results, lang_code)
--
-- Code-completion specialization of substrate.infer. Constrains traversal to
-- the code_completion arena (where Qwen-Coder / DeepSeek-Coder donor edges
-- carry their primed mu) and biases candidate targets toward bpe_token /
-- word_form entities tagged with the requested programming language via
-- substrate.entity_classification + substrate.entity_language.
--
-- Returns the best continuation as a recomposed text composition.
DROP FUNCTION IF EXISTS substrate.complete(BYTEA, INT, INT, TEXT);
CREATE OR REPLACE FUNCTION substrate.complete(
    p_seed_hash    BYTEA,
    p_max_depth    INT  DEFAULT 4,
    p_max_results  INT  DEFAULT 25,
    p_lang_code    TEXT DEFAULT NULL
) RETURNS TABLE (
    answer_text     TEXT,
    seed_count      INT,
    distinct_targets BIGINT,
    best_target_hash BYTEA,
    best_total_mu    DOUBLE PRECISION,
    elapsed_ms       INT
)
LANGUAGE plpgsql
VOLATILE
AS $$
DECLARE
    v_started     TIMESTAMP := clock_timestamp();
    v_arena_id    INT;
    v_lang_id     INT;
    v_seed_count  INT := 0;
    v_targets     BIGINT := 0;
    v_best_hash   BYTEA;
    v_best_mu     DOUBLE PRECISION := 0.0;
    v_answer      TEXT;
BEGIN
    SELECT id INTO v_arena_id
    FROM substrate.significance_context
    WHERE code = 'code_completion';

    -- code_completion arena is open-vocabulary; if absent, fall back to
    -- semantic_relevance so the call still produces a result rather than
    -- erroring on a fresh substrate that hasn't seen the arena yet.
    IF v_arena_id IS NULL THEN
        SELECT id INTO v_arena_id
        FROM substrate.significance_context
        WHERE code = 'semantic_relevance';
    END IF;

    IF p_lang_code IS NOT NULL THEN
        SELECT id INTO v_lang_id
        FROM substrate.language
        WHERE code = p_lang_code;
    END IF;

    -- Seed activation: bpe_token / word_form children of the prompt
    -- composition, optionally filtered by the requested programming
    -- language via entity_language.
    WITH seeds AS (
        SELECT DISTINCT s.child_hash AS h
        FROM substrate.get_composition_children(p_seed_hash) s
        JOIN substrate.entity_classification c ON c.entity_hash = s.child_hash
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        LEFT JOIN substrate.entity_language el
               ON el.entity_hash = s.child_hash
              AND (v_lang_id IS NULL OR el.language_id = v_lang_id)
        WHERE et.code IN ('bpe_token', 'word_form')
          AND (v_lang_id IS NULL OR el.language_id = v_lang_id)
    ),
    seed_count AS (SELECT count(*) AS n FROM seeds)
    SELECT n INTO v_seed_count FROM seed_count;

    IF v_seed_count = 0 THEN
        RETURN QUERY
        SELECT NULL::TEXT, 0, 0::BIGINT, NULL::BYTEA, 0.0::DOUBLE PRECISION,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
        RETURN;
    END IF;

        -- Walk one step out from each seed, accumulating Glicko-2 mu in the
        -- code_completion arena, and pick the best candidate.
        SELECT count(*), max(cands.total_mu),
                     (array_agg(cands.target_hash ORDER BY cands.total_mu DESC))[1]
    INTO v_targets, v_best_mu, v_best_hash
            FROM (
                    SELECT ranked.target_hash, ranked.total_mu
                        FROM (
                                SELECT em_t.entity_hash AS target_hash,
                                             sum(COALESCE(es.mu, 1500.0)) AS total_mu,
                                             row_number() OVER (
                                                     ORDER BY sum(COALESCE(es.mu, 1500.0)) DESC, em_t.entity_hash ASC
                                             ) AS rn
                                    FROM substrate.get_composition_children(p_seed_hash) sq
                                    JOIN substrate.edge_member em_s
                                        ON em_s.entity_hash = sq.child_hash
                                    JOIN substrate.edge e
                                        ON e.edge_type_id = em_s.edge_type_id
                                     AND e.hash = em_s.edge_hash
                                    JOIN substrate.edge_role r_s
                                        ON r_s.id = em_s.edge_role_id
                                     AND r_s.code = 'source'
                                    JOIN substrate.edge_member em_t
                                        ON em_t.edge_type_id = e.edge_type_id
                                     AND em_t.edge_hash = e.hash
                                    JOIN substrate.edge_role r_t
                                        ON r_t.id = em_t.edge_role_id
                                     AND r_t.code = 'target'
                                    LEFT JOIN substrate.edge_significance es
                                        ON es.edge_type_id = e.edge_type_id
                                     AND es.edge_hash = e.hash
                                     AND es.context_type_id = v_arena_id
                                 WHERE em_t.entity_hash <> p_seed_hash
                                 GROUP BY em_t.entity_hash
                        ) ranked
                     WHERE ranked.rn <= GREATEST(COALESCE(p_max_results, 25), 0)
            ) cands;

    IF v_best_hash IS NOT NULL THEN
        v_answer := substrate.recompose_text(v_best_hash, p_max_depth);
    END IF;

    RETURN QUERY
    SELECT COALESCE(v_answer, '')::TEXT,
           v_seed_count,
           v_targets,
           v_best_hash,
           v_best_mu,
           EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
END $$;

COMMENT ON FUNCTION substrate.complete(BYTEA, INT, INT, TEXT) IS
    'Code-completion specialization of substrate.infer. Constrains traversal to the code_completion arena (falls back to semantic_relevance if the arena does not yet exist) and biases candidate targets to bpe_token/word_form entities tagged with the requested programming language via entity_language. Recomposes the best continuation via substrate.recompose_text.';
