-- substrate.create_arena(code TEXT, backfill BOOLEAN DEFAULT TRUE)
--
-- Adds a new arena to substrate.significance_context (the open-vocabulary
-- arena registry). When backfill=TRUE, registers the arena as "needs
-- priming" via substrate.arena_priming_state. Post-W2E the chunked
-- backfill is driven by the StreamingIngestionPipeline's
-- PrimeAllSignificanceAsync end-of-phase pass — it iterates the arena
-- list at call time and loops substrate.prime_unprimed_edges_chunk
-- per arena until it returns 0. No background primer process; no
-- continuous loop. Adding a new arena mid-corpus means it gets primed
-- on the next FlushAsync cycle.
--
-- Why this shape:
--   * The arena CREATE is a single INSERT (set-based, transactional).
--   * The chunked BACKFILL — looping until prime_unprimed_edges_chunk
--     returns 0 — is a "while loop" over expensive set-based work.
--     That loop lives in C# (StreamingIngestionPipeline.
--     PrimeAllSignificanceAsync), not in plpgsql. Per architectural
--     rule: SQL is thin, heavy lifting and control flow live in
--     C/C++ extensions or the C# Compute Facade.
--
-- Returns the new arena's id. Idempotent: a second call with the same
-- code returns the existing id without re-registering.
CREATE OR REPLACE FUNCTION substrate.create_arena(
    p_code     TEXT,
    p_backfill BOOLEAN DEFAULT TRUE
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_id      INT;
    v_existed BOOLEAN := FALSE;
BEGIN
    IF p_code IS NULL OR length(trim(p_code)) = 0 THEN
        RAISE EXCEPTION 'p_code must be a non-empty arena code';
    END IF;

    SELECT id INTO v_id
      FROM substrate.significance_context
     WHERE code = p_code;

    IF v_id IS NOT NULL THEN
        v_existed := TRUE;
    ELSE
        INSERT INTO substrate.significance_context (code)
        VALUES (p_code)
        RETURNING id INTO v_id;
    END IF;

    IF p_backfill AND NOT v_existed THEN
        -- Register the arena as "needs priming". The C# pipeline's
        -- PrimeAllSignificanceAsync end-of-phase pass iterates the arena
        -- list at call time and primes via prime_unprimed_edges_chunk;
        -- this row is the watermark anchor for that loop. INSERT ON
        -- CONFLICT keeps it idempotent against concurrent create_arena
        -- callers.
        INSERT INTO substrate.arena_priming_state (context_type_id)
        VALUES (v_id)
        ON CONFLICT (context_type_id) DO NOTHING;
    END IF;

    RETURN v_id;
END $$;

COMMENT ON FUNCTION substrate.create_arena(TEXT, BOOLEAN) IS
    'Add an arena to substrate.significance_context. With backfill=TRUE, registers it for priming via substrate.arena_priming_state — the C# pipeline''s PrimeAllSignificanceAsync end-of-phase pass picks it up and primes via prime_unprimed_edges_chunk in chunks. SQL stays thin; the chunking loop lives in C#. Returns the arena id; idempotent.';
