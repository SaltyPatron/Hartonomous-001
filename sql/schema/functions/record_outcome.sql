-- substrate.record_outcome(arena_id, winner_target_hash, loser_target_hashes[])
--
-- The engine spec's Step 6 (inference.md): when an inference outcome is
-- known (user accepts/rejects, downstream task succeeds/fails), Glicko-2
-- comparison events update the significance ratings of the edges that
-- supported the selected vs rejected paths.
--
-- For each (winner, loser) pair this function:
--   1. Identifies the strongest (highest current mu) edge in the arena
--      for which the winner target is a member.
--   2. Identifies the strongest (highest current mu) edge in the arena
--      for which a loser target is a member.
--   3. Calls substrate.record_comparison(arena_id, winner_edge, loser_edge),
--      which runs the full Glicko-2 update (mu/sigma/volatility/games on
--      both sides).
--
-- This is the coarse first-pass implementation. A future refinement —
-- "edge-path outcome" — would record comparisons on every edge along the
-- selected path vs every edge along each rejected path, but that requires
-- substrate.infer_topk to expose path geometry. The target-pair update is
-- a faithful first signal that lets the substrate learn from interaction
-- without requiring path retention.
--
-- Returns the number of (winner_edge × loser_edge) comparison events
-- recorded — typically equal to array_length(loser_target_hashes, 1).
DROP FUNCTION IF EXISTS substrate.record_outcome(INT, BYTEA, BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.record_outcome(
    p_arena_id           INT,
    p_winner_target_hash BYTEA,
    p_loser_target_hashes BYTEA[]
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    loser_hash       BYTEA;
    w_edge_type_id   INT;
    w_edge_hash      BYTEA;
    l_edge_type_id   INT;
    l_edge_hash      BYTEA;
    events           INT := 0;
BEGIN
    IF p_winner_target_hash IS NULL OR p_loser_target_hashes IS NULL THEN
        RETURN 0;
    END IF;

    -- Highest-mu edge in this arena for which the winner is a member.
    SELECT em.edge_type_id, em.edge_hash
    INTO w_edge_type_id, w_edge_hash
    FROM substrate.edge_member em
    JOIN substrate.edge_significance es
      ON es.edge_type_id = em.edge_type_id
     AND es.edge_hash    = em.edge_hash
     AND es.context_type_id = p_arena_id
    WHERE em.entity_hash = p_winner_target_hash
    ORDER BY es.mu DESC NULLS LAST
    LIMIT 1;

    IF w_edge_type_id IS NULL THEN
        -- The winner target has no incident edges in this arena — nothing
        -- to update; not an error.
        RETURN 0;
    END IF;

    FOREACH loser_hash IN ARRAY p_loser_target_hashes LOOP
        IF loser_hash IS NULL OR loser_hash = p_winner_target_hash THEN
            CONTINUE;
        END IF;

        SELECT em.edge_type_id, em.edge_hash
        INTO l_edge_type_id, l_edge_hash
        FROM substrate.edge_member em
        JOIN substrate.edge_significance es
          ON es.edge_type_id = em.edge_type_id
         AND es.edge_hash    = em.edge_hash
         AND es.context_type_id = p_arena_id
        WHERE em.entity_hash = loser_hash
        ORDER BY es.mu DESC NULLS LAST
        LIMIT 1;

        IF l_edge_type_id IS NULL THEN
            CONTINUE;
        END IF;

        PERFORM substrate.record_comparison(
            p_arena_id,
            w_edge_type_id, w_edge_hash,
            l_edge_type_id, l_edge_hash);
        events := events + 1;
    END LOOP;

    RETURN events;
END $$;

COMMENT ON FUNCTION substrate.record_outcome(INT, BYTEA, BYTEA[]) IS
    'Coarse target-pair outcome update (Step 6 of inference.md). For each (winner, loser) pair, runs substrate.record_comparison on the strongest edges incident to each target in the requested arena. Returns count of comparisons recorded.';
