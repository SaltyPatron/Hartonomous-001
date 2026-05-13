-- substrate.recall(p_prompt_hash) — the brain's primary direct operation,
-- now structured around hub-intersection rather than max-pool best-target.
--
-- For a prompt's text_composition root:
--   1. Activate seeds: word_form sequence children + their lemma/synset
--      parent compositions (cross-decomposer bridges).
--   2. Cross-reference via substrate.intersect — find entities most strongly
--      intersected across the seeds via edges (in/out), sequence adjacency,
--      and 4D geometric proximity (Fréchet-style bridging of decomposer
--      surface variants).
--   3. Take the top intersected entity. If it's identity-only (synset,
--      lemma, etc.), follow has_gloss/has_text/has_example to a
--      recomposable text_composition. Recompose.
--
-- Cross-decomposer surface bridging is automatic: WordNet "competitor.n.01",
-- Wiktionary "competitor", Tatoeba bare "competitor" inside attested
-- sentences — when their content hashes agree they collapse to one entity;
-- when surfaces differ but trajectories cluster, geometric intersection
-- bridges them.
DROP FUNCTION IF EXISTS substrate.recall(BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.recall(
    p_prompt_hash       BYTEA,
    p_max_depth         INT              DEFAULT 3,
    p_top_k             INT              DEFAULT 25,
    p_frechet_threshold DOUBLE PRECISION DEFAULT 0.25
) RETURNS TABLE (
    answer        TEXT,
    target_hash   BYTEA,
    confidence    DOUBLE PRECISION,
    seed_count    INT,
    target_count  BIGINT,
    elapsed_ms    INT
)
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_started      TIMESTAMP := clock_timestamp();
    v_word_form_id INT;
    v_seeds        BYTEA[];
    v_best_hash    BYTEA;
    v_best_score   DOUBLE PRECISION;
    v_best_seeds   INT;
    v_target_count BIGINT := 0;
    v_answer       TEXT;
    v_text_hash    BYTEA;
BEGIN
    SELECT id INTO v_word_form_id FROM substrate.entity_type WHERE code = 'word_form';

    -- Seed activation: prompt's word_form composition children + their
    -- lemma/synset parent compositions.
    SELECT array_agg(DISTINCT h)
    INTO v_seeds
    FROM (
        SELECT s.child_hash AS h
        FROM substrate.get_composition_children(p_prompt_hash) s
        JOIN substrate.entity_classification c
          ON c.entity_hash = s.child_hash
         AND c.entity_type_id = v_word_form_id
        UNION
        SELECT s.parent_hash AS h
        FROM substrate.get_composition_children(p_prompt_hash) sd
        JOIN substrate.composition_parents(sd.child_hash) s ON TRUE
        JOIN substrate.entity_classification c ON c.entity_hash = s.parent_hash
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        WHERE et.code IN ('lemma', 'synset')
          AND s.parent_hash <> p_prompt_hash
    ) seeds;

    IF v_seeds IS NULL OR array_length(v_seeds, 1) = 0 THEN
        RETURN QUERY SELECT
            NULL::TEXT, NULL::BYTEA, 0.0::DOUBLE PRECISION,
            0, 0::BIGINT,
            EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
        RETURN;
    END IF;

    -- Hub intersection across seeds. Top-1 is the substrate's most
    -- structurally-intersected entity for this prompt.
    SELECT i.neighbor_hash, i.score, i.seed_count
    INTO v_best_hash, v_best_score, v_best_seeds
    FROM substrate.intersect(v_seeds, NULL, 1, p_frechet_threshold) i
    LIMIT 1;

    SELECT count(*)
    INTO v_target_count
    FROM substrate.intersect(v_seeds, NULL, 1000, p_frechet_threshold);

    IF v_best_hash IS NULL THEN
        RETURN QUERY SELECT
            NULL::TEXT, NULL::BYTEA, 0.0::DOUBLE PRECISION,
            COALESCE(array_length(v_seeds, 1), 0), v_target_count,
            EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
        RETURN;
    END IF;

    -- Try direct recompose first (works if best target is itself a
    -- text_composition).
    v_answer := substrate.recompose_text(v_best_hash, p_max_depth);

    -- If identity-only, bridge to the canonical surface text via has_gloss /
    -- has_text / has_etymology / has_example edges.
    IF v_answer IS NULL OR length(v_answer) = 0 THEN
        SELECT em_t.entity_hash
        INTO v_text_hash
        FROM substrate.edge e
        JOIN substrate.edge_type et ON et.id = e.edge_type_id
        JOIN substrate.edge_member em_s
          ON em_s.edge_type_id = e.edge_type_id
         AND em_s.edge_hash    = e.hash
        JOIN substrate.edge_role r_s ON r_s.id = em_s.edge_role_id AND r_s.code = 'source'
        JOIN substrate.edge_member em_t
          ON em_t.edge_type_id = e.edge_type_id
         AND em_t.edge_hash    = e.hash
        JOIN substrate.edge_role r_t ON r_t.id = em_t.edge_role_id AND r_t.code = 'target'
        JOIN substrate.entity_classification c_t ON c_t.entity_hash = em_t.entity_hash
        JOIN substrate.entity_type et_t ON et_t.id = c_t.entity_type_id
        WHERE em_s.entity_hash = v_best_hash
          AND et.code IN ('has_gloss', 'has_example', 'has_text', 'has_etymology', 'has_pronunciation')
          AND et_t.code = 'text_composition'
          AND EXISTS (SELECT 1 FROM substrate.get_composition_children(em_t.entity_hash) LIMIT 1)
        ORDER BY
            CASE et.code
                WHEN 'has_gloss'     THEN 0
                WHEN 'has_text'      THEN 1
                WHEN 'has_etymology' THEN 2
                WHEN 'has_example'   THEN 3
                ELSE 9
            END
        LIMIT 1;

        IF v_text_hash IS NOT NULL THEN
            v_answer := substrate.recompose_text(v_text_hash, p_max_depth);
        END IF;
    END IF;

    RETURN QUERY SELECT
        v_answer,
        v_best_hash,
        v_best_score,
        COALESCE(array_length(v_seeds, 1), 0),
        v_target_count,
        EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
END $$;

COMMENT ON FUNCTION substrate.recall(BYTEA, INT, INT, DOUBLE PRECISION) IS
    'Brain''s primary direct operation. Activates seeds from prompt''s text_composition, runs hub intersection (substrate.intersect over edges + sequence + 4D geometric proximity), takes the top intersected entity, recomposes its surface text (directly or via has_gloss/has_text/has_example bridge).';
