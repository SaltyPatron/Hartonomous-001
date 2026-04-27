-- 0064_prime_edge_significance_typed_null.up.sql
--
-- Fixes a constraint failure in substrate.prime_edge_significance_for_staging
-- (defined in migration 0052). Under WordNet-scale batch volume the function
-- intermittently INSERTs rows where entity_id contains the bit pattern of
-- the volatility literal 0.06 (= 0x3FAEB851EB851EB8 = 4588807732320345784
-- as int8). With both entity_id and edge_id non-null, the table's
-- significance_check constraint ((entity_id IS NOT NULL) <> (edge_id IS NOT
-- NULL)) rejects the row and the entire batch transaction rolls back.
--
-- Reproduction signature in the PG log:
--     ERROR: new row for relation "significance_lexical" violates check
--         constraint "significance_check"
--     DETAIL: Failing row contains (..., 4588807732320345784, ..., 0.06, 0).
--     PL/pgSQL function substrate.prime_edge_significance_for_staging()
--         line 5 at SQL statement
--
-- Root cause is PostgreSQL's resolution of an UNTYPED NULL in a SELECT list
-- alongside same-position float literals: under specific cached-plan + LIST-
-- partition routing conditions, the planner can resolve the untyped NULL's
-- type from a subsequent float column instead of from the INSERT target,
-- which lets the float column's bit pattern leak into the int8 slot. CAST
-- the NULL explicitly to bigint and the ambiguity disappears — every plan
-- the executor produces sees a typed NULL bigint and routes it correctly to
-- entity_id regardless of partition pruning state.
--
-- Same fix applied to substrate.backfill_edge_significance_for_arena which
-- uses the same SELECT-NULL-with-floats pattern.

CREATE OR REPLACE FUNCTION substrate.prime_edge_significance_for_staging()
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_inserted BIGINT;
BEGIN
    INSERT INTO substrate.significance
        (entity_id, edge_id, context_type_id, mu, sigma, volatility, games)
    SELECT CAST(NULL AS BIGINT), e.id, sc.id, p.initial_mu, 350.0, 0.06, 0
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
    SELECT CAST(NULL AS BIGINT), e.id, v_context_id, p.initial_mu, 350.0, 0.06, 0
      FROM substrate.edge e
      JOIN substrate.provenance p ON p.id = e.provenance_id
        ON CONFLICT DO NOTHING;

    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    RETURN v_inserted;
END $$;
