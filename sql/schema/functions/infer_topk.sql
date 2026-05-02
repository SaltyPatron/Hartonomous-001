-- substrate.infer_topk(p_doc_hash, p_max_depth, p_max_results, p_top_k)
--
-- Top-K variant of substrate.infer. Same forward pass — seed activation
-- via prompt's word_form children + lemma/synset parents, cross-arena A*
-- via traverse_astar, max-pool by target hash — but instead of returning
-- only the best target, returns the K highest-mu targets with each one's
-- recomposed text. The Gödel Engine uses this for:
--
--   * Self-Consistency voting: a target reached by multiple traversal
--     paths (same hash recurs across seed × arena combinations) accrues
--     a higher vote count; agreement boosts confidence.
--   * Tree-of-Thought selection: each top-K row is a candidate "thought
--     branch" the engine evaluates by significance vs path coherence.
--   * Honest abstention threshold: when no top-K row exceeds a confidence
--     floor, the engine abstains rather than fabricating.
--
-- Hash-only signature throughout. recompose_text walks substrate.sequence
-- to codepoint leaves; each row is a real recomposition of substrate
-- content, not a sampled string.
DROP FUNCTION IF EXISTS substrate.infer_topk(BYTEA, INT, INT, INT);
CREATE OR REPLACE FUNCTION substrate.infer_topk(
    p_doc_hash    bytea,
    p_max_depth   INT  DEFAULT 5,
    p_max_results INT  DEFAULT 50,
    p_top_k       INT  DEFAULT 5
) RETURNS TABLE (
    rank             INT,
    target_hash      bytea,
    total_mu         DOUBLE PRECISION,
    path_count       BIGINT,
    recomposed_text  TEXT
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_word_form_id INT;
BEGIN
    SELECT id INTO v_word_form_id FROM substrate.entity_type WHERE code = 'word_form';

    -- Seeds: prompt's word_form-classified sequence children + their
    -- lemma/synset parent compositions. Same seed activation as substrate.infer.
    CREATE TEMP TABLE IF NOT EXISTS _topk_seeds (seed_hash bytea PRIMARY KEY) ON COMMIT DROP;
    TRUNCATE _topk_seeds;
    INSERT INTO _topk_seeds (seed_hash)
    WITH direct_seeds AS (
        SELECT DISTINCT s.child_hash AS h
        FROM substrate.sequence s
        JOIN substrate.entity_classification c
          ON c.entity_hash = s.child_hash
         AND c.entity_type_id = v_word_form_id
        WHERE s.parent_hash = p_doc_hash
    ),
    indirect_seeds AS (
        SELECT DISTINCT s.parent_hash AS h
        FROM direct_seeds d
        JOIN substrate.sequence s ON s.child_hash = d.h
        JOIN substrate.entity_classification c ON c.entity_hash = s.parent_hash
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        WHERE et.code IN ('lemma', 'synset')
          AND s.parent_hash <> p_doc_hash
    )
    SELECT h FROM direct_seeds
    UNION
    SELECT h FROM indirect_seeds
    ON CONFLICT (seed_hash) DO NOTHING;

    -- Pool: cross-arena traverse_astar with both max(mu) AND count(*).
    -- path_count = how many distinct (seed, arena) traversals reached this
    -- target. Self-Consistency: high path_count = independent corroboration.
    CREATE TEMP TABLE IF NOT EXISTS _topk_pooled (
        target_hash bytea PRIMARY KEY,
        best_mu     DOUBLE PRECISION,
        path_count  BIGINT
    ) ON COMMIT DROP;
    TRUNCATE _topk_pooled;
    INSERT INTO _topk_pooled (target_hash, best_mu, path_count)
    SELECT
        rp.target_hash,
        MAX(rp.total_mu) AS best_mu,
        COUNT(*)         AS path_count
    FROM (
        SELECT
            t.target_entity_hash AS target_hash,
            t.total_mu
        FROM _topk_seeds AS s
        CROSS JOIN substrate.significance_context AS a
        CROSS JOIN LATERAL public.traverse_astar(
            s.seed_hash,
            NULL::INT,
            a.id,
            p_max_depth, p_max_results, NULL::DOUBLE PRECISION
        ) AS t
        WHERE t.target_entity_hash IS NOT NULL
    ) rp
    GROUP BY rp.target_hash;

    -- Top-K with stable tie-break (best_mu DESC, path_count DESC,
    -- target_hash ASC). Each row is recomposed via substrate.recompose_text
    -- — all-substrate generation, deterministic across runs.
    RETURN QUERY
    SELECT
        ROW_NUMBER() OVER (ORDER BY p.best_mu DESC, p.path_count DESC, p.target_hash)::INT AS rank,
        p.target_hash,
        p.best_mu,
        p.path_count,
        substrate.recompose_text(p.target_hash, p_max_depth)
    FROM _topk_pooled p
    ORDER BY p.best_mu DESC, p.path_count DESC, p.target_hash
    LIMIT p_top_k;
END $$;

COMMENT ON FUNCTION substrate.infer_topk(BYTEA, INT, INT, INT) IS
    'Top-K targets from a forward pass over the prompt. Hash-only. Returns rank, target_hash, total_mu, path_count, recomposed_text. The Gödel Engine consumes this for Self-Consistency voting, ToT branch selection, and honest-abstention thresholds.';
