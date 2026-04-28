-- substrate.prime_edge_significance_for_staging()
-- substrate.backfill_edge_significance_for_arena(p_context_code TEXT)
--
-- Open-vocabulary edge significance priming. Two halves:
--
--   prime_edge_significance_for_staging(): per-batch. Reads from the
--   per-transaction TEMP table `staging_edge` (already populated by
--   NpgsqlIngestionPipeline.CreateEdgesAsync) and writes one
--   substrate.edge_significance row per (edge × arena) using each edge's
--   provenance trust prior. Cross-products against every arena currently
--   in substrate.significance_context — never a hardcoded list (AP-1).
--
--   backfill_edge_significance_for_arena(): called when a new arena is
--   added to substrate.significance_context. Backfills every existing
--   edge in the new arena using its provenance.initial_mu.
--
-- Hash-as-PK throughout: composite (edge_type_id, edge_hash) addresses
-- substrate.edge_significance directly. ON CONFLICT DO NOTHING keeps
-- both functions idempotent under repeated calls.
--
-- The mu primer is what makes substrate A* meaningful: without it,
-- COALESCE(mu, 1500.0) in the traversal degenerates to uniform-cost BFS.
-- Source-trust ladder: unicode_consortium (2000) > sil_international
-- (2000) > princeton_wordnet (1800) > omwn / universaldependencies (1600)
-- > huggingface_model (1500) > wiktextract (1400) > system_computed (1300)
-- > tatoeba (1200) > user_session (1000). See provenance seed.
CREATE OR REPLACE FUNCTION substrate.prime_edge_significance_for_staging()
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_inserted BIGINT;
BEGIN
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
    SELECT sc.id, e.edge_type_id, e.hash, p.initial_mu, 350.0, 0.06, 0
      FROM staging_edge s
      JOIN substrate.edge e
        ON e.edge_type_id = s.edge_type_id
       AND e.hash         = s.hash
      JOIN substrate.provenance p ON p.id = e.provenance_id
      CROSS JOIN substrate.significance_context sc
        ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING;

    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    RETURN v_inserted;
END $$;

COMMENT ON FUNCTION substrate.prime_edge_significance_for_staging() IS
    'Per-batch: prime substrate.edge_significance from provenance.initial_mu across every arena currently in significance_context, for every edge in the per-transaction staging_edge TEMP table. Open-vocabulary; no cherry-picking.';

CREATE OR REPLACE FUNCTION substrate.backfill_edge_significance_for_arena(p_context_code TEXT)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id INT;
    v_inserted   BIGINT;
BEGIN
    SELECT id INTO v_context_id
      FROM substrate.significance_context
     WHERE code = p_context_code;
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'Unknown significance_context code: %', p_context_code;
    END IF;

    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
    SELECT v_context_id, e.edge_type_id, e.hash, p.initial_mu, 350.0, 0.06, 0
      FROM substrate.edge e
      JOIN substrate.provenance p ON p.id = e.provenance_id
        ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING;

    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    RETURN v_inserted;
END $$;

COMMENT ON FUNCTION substrate.backfill_edge_significance_for_arena(TEXT) IS
    'When a new arena is added to substrate.significance_context, backfill substrate.edge_significance rows for every existing edge using each edge''s provenance trust prior. Idempotent.';
