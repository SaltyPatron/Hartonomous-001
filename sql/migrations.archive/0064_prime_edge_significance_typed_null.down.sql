-- 0064_prime_edge_significance_typed_null.down.sql
--
-- Revert the typed-NULL versions of the priming and backfill functions back
-- to migration 0052's untyped NULL form. Note: the untyped form is the form
-- that exhibited the constraint violation under WordNet-scale load (per the
-- 0064 up-migration's commentary), so down-migrating reintroduces that
-- defect. Provided for replay completeness only.

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
