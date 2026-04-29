-- substrate.initialize_significance(p_arena_id INT)
--
-- Prime a single arena's substrate.edge_significance partition from
-- scratch by looping substrate.prime_unprimed_edges_chunk(p_arena_id, …)
-- until it reports 0 newly inserted rows. Returns the total rows primed.
--
-- This is the "new arena added today, backfill against every existing
-- edge" path (.claude/rules/15 § "Arenas are open-vocabulary"): when
-- an operator adds a new code to substrate.significance_context — e.g.
-- pragmatic_register, English-medical-pharmacology, Qwen3-vs-Llama3-
-- attention — call this once with the new arena's id to fill the
-- partition. Cross-product against existing edges happens via
-- prime_unprimed_edges_chunk's watermark scan over the
-- (edge_type_id, hash) PK of substrate.edge.
--
-- Idempotent: calling repeatedly on a fully-primed arena returns 0.

CREATE OR REPLACE FUNCTION substrate.initialize_significance(p_arena_id INT)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_chunk    BIGINT;
    v_total    BIGINT := 0;
    v_chunk_sz CONSTANT INT := 4096;
    v_iter     INT := 0;
    v_max_iter CONSTANT INT := 1000000;  -- safety bound (~4G edges)
BEGIN
    IF p_arena_id IS NULL THEN
        RAISE EXCEPTION 'p_arena_id must not be NULL';
    END IF;

    IF NOT EXISTS (
        SELECT 1
          FROM substrate.significance_context
         WHERE id = p_arena_id
    ) THEN
        RAISE EXCEPTION 'Unknown significance_context id: %', p_arena_id;
    END IF;

    LOOP
        v_chunk := substrate.prime_unprimed_edges_chunk(p_arena_id, v_chunk_sz);
        v_total := v_total + v_chunk;
        EXIT WHEN v_chunk = 0;
        v_iter := v_iter + 1;
        IF v_iter > v_max_iter THEN
            RAISE EXCEPTION 'initialize_significance exceeded max iterations (% chunks of %)',
                v_max_iter, v_chunk_sz;
        END IF;
    END LOOP;

    RETURN v_total;
END $$;

COMMENT ON FUNCTION substrate.initialize_significance(INT) IS
    'Prime a single arena from scratch by looping prime_unprimed_edges_chunk until 0. Idempotent. Used for new arenas added to significance_context after edges already exist.';
