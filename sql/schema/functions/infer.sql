-- substrate.infer(prompt_doc_hash, max_depth, max_results)
--
-- The forward pass — substrate-side, single round-trip from C#.
-- Hash-only entity references throughout (Phase C unification).
--
-- Steps 1-4 of docs/specs/engine/inference.md, executed inside one PG
-- function:
--   1. Seed activation: collect the prompt's word_form children from
--      composition physicality metadata + cross-classification matches via
--      substrate.entity_classification (a hash classified as "lemma" by
--      WordNet AND as "word_form" by Tatoeba is the SAME hash; A* gets
--      both classifications' edge sets implicitly).
--   2. Cross-arena A* via the C extension's traverse_astar (called per
--      arena × per seed). NOTE: the C extension's signature drops
--      entity_type_id with the schema collapse — caller passes hash only.
--   3. Max-pool path significance per terminal entity hash.
--   4. Recompose: walk highest-significance terminal via substrate.recompose_text.
CREATE OR REPLACE FUNCTION substrate.infer(
    p_doc_hash    bytea,
    p_max_depth   INT  DEFAULT 5,
    p_max_results INT  DEFAULT 50
) RETURNS TABLE (
    answer_text         TEXT,
    seed_count          INT,
    distinct_targets    BIGINT,
    best_target_hash    bytea,
    best_total_mu       DOUBLE PRECISION,
    elapsed_ms          INT
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_started      TIMESTAMP := clock_timestamp();
    v_seed_count   INT := 0;
    v_target_count BIGINT := 0;
    v_best_hash    bytea;
    v_best_mu      DOUBLE PRECISION;
    v_answer       TEXT;
    v_word_form_id INT;
BEGIN
    SELECT id INTO v_word_form_id FROM substrate.entity_type WHERE code = 'word_form';

    -- Materialize seeds: prompt's word_form-classified composition children
    -- + the prompt itself + parent compositions of those word_forms.
    CREATE TEMP TABLE IF NOT EXISTS _infer_seeds (seed_hash bytea PRIMARY KEY) ON COMMIT DROP;
    TRUNCATE _infer_seeds;
    INSERT INTO _infer_seeds (seed_hash)
    WITH direct_seeds AS (
        SELECT DISTINCT s.child_hash AS h
        FROM substrate.get_composition_children(p_doc_hash) s
        JOIN substrate.entity_classification c
          ON c.entity_hash = s.child_hash
         AND c.entity_type_id = v_word_form_id
    ),
    -- Inverse-composition: lemma / synset compositions that contain the
    -- prompt's word_form hashes as children. These are the substrate's
    -- "where else does this word appear" bridges into the rich graph.
    indirect_seeds AS (
        SELECT DISTINCT s.parent_hash AS h
        FROM direct_seeds d
        JOIN substrate.composition_parents(d.h) s ON TRUE
        JOIN substrate.entity_classification c ON c.entity_hash = s.parent_hash
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        WHERE et.code IN ('lemma', 'synset')
          AND s.parent_hash <> p_doc_hash
    )
    SELECT h FROM direct_seeds
    UNION
    SELECT h FROM indirect_seeds
    ON CONFLICT (seed_hash) DO NOTHING;

    SELECT count(*) INTO v_seed_count FROM _infer_seeds;

    -- Pool: cross-arena traverse_astar fan-out, max-pool by target hash.
    CREATE TEMP TABLE IF NOT EXISTS _infer_pooled (
        target_hash bytea PRIMARY KEY,
        best_mu     DOUBLE PRECISION
    ) ON COMMIT DROP;
    TRUNCATE _infer_pooled;
    INSERT INTO _infer_pooled (target_hash, best_mu)
    SELECT
        rp.target_hash,
        MAX(rp.total_mu) AS best_mu
    FROM (
        SELECT
            t.target_entity_hash AS target_hash,
            t.total_mu
        FROM _infer_seeds AS s
        CROSS JOIN substrate.significance_context AS a
        CROSS JOIN LATERAL public.traverse_astar(
            s.seed_hash,
            NULL::INT,
            a.id,
            p_max_depth, p_max_results, NULL::DOUBLE PRECISION
        ) AS t
        WHERE t.target_entity_hash IS NOT NULL
    ) rp
    GROUP BY rp.target_hash
    ON CONFLICT (target_hash) DO UPDATE SET best_mu = GREATEST(_infer_pooled.best_mu, EXCLUDED.best_mu);

    SELECT count(*) INTO v_target_count FROM _infer_pooled;

    SELECT p.target_hash, p.best_mu
    INTO v_best_hash, v_best_mu
    FROM _infer_pooled p
    ORDER BY p.best_mu DESC, p.target_hash
    LIMIT 1;

    IF v_best_hash IS NOT NULL THEN
        v_answer := substrate.recompose_text(v_best_hash, p_max_depth);
    END IF;

    RETURN QUERY SELECT
        v_answer,
        v_seed_count,
        v_target_count,
        v_best_hash,
        v_best_mu,
        EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
END $$;

COMMENT ON FUNCTION substrate.infer(BYTEA, INT, INT) IS
    'Forward pass — Steps 1-4 of inference.md. Hash-only signature (Phase C unification). Cross-arena A* + max-pool + recompose. Single PG round-trip.';

-- Drop old signature.
DROP FUNCTION IF EXISTS substrate.infer(INT, substrate.hash_value, INT, INT);
