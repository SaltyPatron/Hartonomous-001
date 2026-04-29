-- substrate.prune_significance(
--     p_min_mu    DOUBLE PRECISION,
--     p_max_sigma DOUBLE PRECISION,
--     p_dry_run   BOOLEAN)
--
-- Remove substrate.edge_significance rows whose μ has fallen below
-- p_min_mu OR whose σ has stayed above p_max_sigma after enough games.
-- Either threshold may be NULL to disable that side of the predicate.
-- Returns the number of rows pruned (or, when p_dry_run = TRUE, the
-- number that would be pruned).
--
-- Pruning never deletes from substrate.edge — only from edge_significance,
-- and only the (arena × edge) cells that have lost confidence in this
-- arena. The edge itself remains in the substrate; another arena may still
-- rate it strongly. This matches the open-vocabulary discipline (.claude/
-- rules/15 § "Arenas are open-vocabulary"): an edge can be pruned in
-- arena A while remaining alive in arena B.
--
-- Bulk DELETE — set-based, no per-row CALL loop (root CLAUDE.md "Batch
-- everything"). Single round-trip per call.

CREATE OR REPLACE FUNCTION substrate.prune_significance(
    p_min_mu    DOUBLE PRECISION DEFAULT NULL,
    p_max_sigma DOUBLE PRECISION DEFAULT NULL,
    p_dry_run   BOOLEAN          DEFAULT FALSE
)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_count BIGINT;
BEGIN
    IF p_min_mu IS NULL AND p_max_sigma IS NULL THEN
        RETURN 0;  -- no predicate → no-op (refuse to delete the table)
    END IF;

    IF p_dry_run THEN
        SELECT COUNT(*)
          INTO v_count
          FROM substrate.edge_significance
         WHERE (p_min_mu    IS NULL OR mu    < p_min_mu)
           AND (p_max_sigma IS NULL OR sigma > p_max_sigma);
        RETURN v_count;
    END IF;

    DELETE FROM substrate.edge_significance
     WHERE (p_min_mu    IS NULL OR mu    < p_min_mu)
       AND (p_max_sigma IS NULL OR sigma > p_max_sigma);

    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END $$;

COMMENT ON FUNCTION substrate.prune_significance(DOUBLE PRECISION, DOUBLE PRECISION, BOOLEAN) IS
    'Remove low-confidence rows from substrate.edge_significance: μ < p_min_mu AND σ > p_max_sigma (each NULL disables that side). p_dry_run = TRUE returns the would-prune count without deleting. NULL/NULL is a no-op refusing to delete everything. Returns rows pruned (or to-be-pruned).';
