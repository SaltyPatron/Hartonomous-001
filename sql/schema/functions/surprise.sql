-- substrate.surprise(p_top_k) — open-ended fact selection.
--
-- For prompts that don't point at a specific entity ("tell me something
-- interesting"), direct recall is the wrong operation. The brain instead
-- picks structurally interesting entities from the substrate:
--   * high mu (well-corroborated)
--   * synset-tier (carries gloss text via has_gloss)
--   * not yet served in the current user_session (avoids repetition)
--
-- Returns up to p_top_k candidate facts, each with its associated text
-- (recomposed gloss) and confidence. The caller picks whichever fits the
-- prompt's framing.
DROP FUNCTION IF EXISTS substrate.surprise(INT, INT);
CREATE OR REPLACE FUNCTION substrate.surprise(
    p_top_k       INT DEFAULT 5,
    p_max_depth   INT DEFAULT 100000
) RETURNS TABLE (
    rank          INT,
    target_hash   BYTEA,
    confidence    DOUBLE PRECISION,
    answer        TEXT
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH high_mu_synsets AS (
        SELECT
            c.entity_hash,
            -- Pick the highest mu across all arenas for ranking.
            MAX(es.mu) AS best_mu
        FROM substrate.entity_classification c
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        JOIN substrate.edge_member em ON em.entity_hash = c.entity_hash
        JOIN substrate.edge_significance es
          ON es.edge_type_id = em.edge_type_id
         AND es.edge_hash    = em.edge_hash
        WHERE et.code = 'synset'
        GROUP BY c.entity_hash
        ORDER BY best_mu DESC NULLS LAST, c.entity_hash
        LIMIT p_top_k * 4    -- oversample so we can filter to ones with glosses
    ),
    with_gloss AS (
        SELECT
            h.entity_hash,
            h.best_mu,
            -- Find the gloss text_composition this synset has_gloss to.
            (SELECT em_t.entity_hash
               FROM substrate.edge e
               JOIN substrate.edge_type et2 ON et2.id = e.edge_type_id
               JOIN substrate.edge_member em_s
                 ON em_s.edge_type_id = e.edge_type_id
                AND em_s.edge_hash    = e.hash
               JOIN substrate.edge_role r_s ON r_s.id = em_s.edge_role_id AND r_s.code = 'source'
               JOIN substrate.edge_member em_t
                 ON em_t.edge_type_id = e.edge_type_id
                AND em_t.edge_hash    = e.hash
               JOIN substrate.edge_role r_t ON r_t.id = em_t.edge_role_id AND r_t.code = 'target'
              WHERE em_s.entity_hash = h.entity_hash
                AND et2.code = 'has_gloss'
                AND EXISTS (SELECT 1 FROM substrate.get_composition_children(em_t.entity_hash) LIMIT 1)
              LIMIT 1
            ) AS gloss_hash
        FROM high_mu_synsets h
    )
    -- Gate 1 reopened item #36 (2026-05-18): substrate.recompose_text removed.
    -- The recomposition surface is now the C# bulk-tier walker
    -- (Hartonomous.Core.Recomposition.BulkTierContentWalk). This SQL function
    -- returns the gloss target's entity hash + NULL answer; callers must
    -- pass the gloss_hash through ContentRecomposer.RecomposeAsync to
    -- materialize the surface text. p_max_depth is preserved on the
    -- signature for compatibility (unused).
    SELECT
        ROW_NUMBER() OVER (ORDER BY w.best_mu DESC NULLS LAST, w.entity_hash)::INT AS rank,
        w.gloss_hash AS target_hash,
        w.best_mu    AS confidence,
        NULL::TEXT   AS answer
    FROM with_gloss w
    WHERE w.gloss_hash IS NOT NULL
    ORDER BY w.best_mu DESC NULLS LAST, w.entity_hash
    LIMIT p_top_k;
$$;

COMMENT ON FUNCTION substrate.surprise(INT, INT) IS
    'Open-ended fact selector. Picks up to p_top_k high-mu synsets that have associated gloss text. Returns the gloss target hash and confidence; the answer column is NULL — caller materializes the surface text via the C# ContentRecomposer (Gate 1 #36, 2026-05-18). p_max_depth preserved on signature for compatibility.';
