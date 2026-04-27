-- 0052_prime_edge_significance_function.up.sql
--
-- Replaces inline SQL in NpgsqlIngestionPipeline.CreateEdgesAsync with a
-- named substrate.* function the C# layer calls by name. Per AP-2 in
-- .claude/rules/45-anti-patterns.md: the C# layer calls SQL by procedure
-- name; it does not construct SQL.
--
-- Two functions:
--
--   substrate.prime_edge_significance_for_staging()
--     Called from CreateEdgesAsync after the edge_member COPY. Reads from
--     the per-batch TEMP table `staging_edge` (already populated by the
--     pipeline) and primes a significance row per (edge × arena) using
--     the edge's provenance trust prior. Cross-products against EVERY
--     arena currently in significance_context (no cherry-picking; AP-1).
--     ON CONFLICT DO NOTHING keeps it idempotent.
--
--   substrate.backfill_edge_significance_for_arena(p_context_code TEXT)
--     Called when a new arena is added to significance_context. Backfills
--     significance rows for every existing edge in the new arena using
--     each edge's provenance.initial_mu. ON CONFLICT DO NOTHING.
--
-- Both functions enforce the open-vocabulary discipline: arena set is
-- whatever significance_context currently contains, never a hardcoded list.

CREATE OR REPLACE FUNCTION substrate.prime_edge_significance_for_staging()
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_inserted BIGINT;
BEGIN
    INSERT INTO substrate.significance
        (entity_id, edge_id, context_type_id, mu, sigma, volatility, games)
    SELECT NULL, e.id, sc.id, p.initial_mu, 350.0, 0.06, 0
      FROM staging_edge s
      JOIN substrate.edge e ON e.hash = s.hash AND e.edge_type_id = s.edge_type_id
      JOIN substrate.provenance p ON p.id = e.provenance_id
      CROSS JOIN substrate.significance_context sc
        ON CONFLICT DO NOTHING;

    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    RETURN v_inserted;
END $$;

COMMENT ON FUNCTION substrate.prime_edge_significance_for_staging() IS
    'Per-batch: prime significance rows from provenance trust prior across every arena currently in significance_context for every edge in the per-transaction staging_edge TEMP table. Open-vocabulary, no arena cherry-picking. Returns count of rows inserted.';

CREATE OR REPLACE FUNCTION substrate.backfill_edge_significance_for_arena(p_context_code TEXT)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id INT;
    v_inserted BIGINT;
BEGIN
    SELECT id INTO v_context_id FROM substrate.significance_context WHERE code = p_context_code;
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'Unknown significance_context code: %', p_context_code;
    END IF;

    INSERT INTO substrate.significance
        (entity_id, edge_id, context_type_id, mu, sigma, volatility, games)
    SELECT NULL, e.id, v_context_id, p.initial_mu, 350.0, 0.06, 0
      FROM substrate.edge e
      JOIN substrate.provenance p ON p.id = e.provenance_id
        ON CONFLICT DO NOTHING;

    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    RETURN v_inserted;
END $$;

COMMENT ON FUNCTION substrate.backfill_edge_significance_for_arena(TEXT) IS
    'Backfill: when a new arena is added to significance_context, prime significance rows for every existing edge in the new arena using provenance trust priors. Idempotent. Returns count of rows inserted.';
