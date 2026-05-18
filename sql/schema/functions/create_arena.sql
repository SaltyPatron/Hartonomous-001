-- substrate.create_arena(code TEXT, backfill BOOLEAN DEFAULT TRUE)
--
-- Adds a new arena to substrate.significance_context (the open-vocabulary
-- arena registry). The backfill parameter is retained for call-site
-- compatibility but no longer registers a watermark — drain-completion
-- post-passes were deleted per AP-37. Edge-significance priors are now
-- emitted inline at edge-emit by the bundled-emit pipeline, which
-- cross-products against every arena currently in significance_context
-- at pipeline startup. New arenas created mid-corpus are picked up the
-- next time a StreamingIngestionPipeline opens; new edges from that
-- point on prime against the new arena. Back-priming over edges that
-- already landed before the arena was created is left to the practitioner
-- (re-emit affected edges, or re-run the relevant phase).
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
    v_id INT;
BEGIN
    IF p_code IS NULL OR length(trim(p_code)) = 0 THEN
        RAISE EXCEPTION 'p_code must be a non-empty arena code';
    END IF;

    -- p_backfill kept for call-site compatibility; ignored on purpose.
    PERFORM p_backfill;

    SELECT id INTO v_id
      FROM substrate.significance_context
     WHERE code = p_code;

    IF v_id IS NULL THEN
        INSERT INTO substrate.significance_context (code)
        VALUES (p_code)
        RETURNING id INTO v_id;
    END IF;

    RETURN v_id;
END $$;

COMMENT ON FUNCTION substrate.create_arena(TEXT, BOOLEAN) IS
    'Add an arena to substrate.significance_context. Per AP-37, no post-pass priming — edge-significance priors are emitted inline at edge-emit by the bundled-emit pipeline. The backfill argument is retained for call-site compatibility and ignored. Returns the arena id; idempotent.';
