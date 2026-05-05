-- substrate.populate_codepoint_atoms(provenance_code TEXT, trust_mu FLOAT8)
--
-- Replaces the C# UCD/UCA decomposer's per-codepoint emission loop with
-- a substrate-side bulk INSERT driven by the extension's embedded UCD
-- 17.0.0 tables. Inserts ~1,114,112 codepoint entities + classifications
-- + S^3 physicalities + significance rows — same substrate state,
-- ~30× the speed of XML parsing.
--
-- Pre-requisites:
--   * substrate.entity, substrate.entity_classification, substrate.physicality,
--     substrate.entity_significance tables exist (bootstrap satisfied).
--   * Extension hartonomous installed (CREATE EXTENSION hartonomous).
--   * Reference rows seeded for: provenance, entity_type=codepoint,
--     physicality_type=s3_position, significance_context=source_authority.
--
-- Determinism (Law #6): substrate.cp_hash(cp) is the BLAKE3 of the rune's
-- big-endian 4-byte encoding, precomputed at extension build time;
-- substrate.cp_centroid(cp) is the Super-Fibonacci S^3 point anchored by
-- UCA-sorted index, also precomputed. Same UCD version → byte-identical
-- substrate state across runs.
--
-- IMPLEMENTATION NOTE — scalar cp_* accessors over generate_series.
--
-- This function intentionally uses set-based INSERT...SELECT statements and
-- avoids row loops/chunk loops in plpgsql. The extension scalar accessors run
-- in per-tuple ExprContext and preserve deterministic values for all rows.
--
-- Returns the count of codepoints processed.
CREATE OR REPLACE FUNCTION substrate.populate_codepoint_atoms(
    p_provenance_code TEXT   DEFAULT 'unicode_consortium',
    p_trust_mu        FLOAT8 DEFAULT NULL
)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_provenance_id    INT;
    v_codepoint_etype  INT;
    v_s3_phys_type     INT;
    v_source_auth_ctx  INT;
    v_initial_mu       FLOAT8;
BEGIN

    SELECT id, COALESCE(p_trust_mu, initial_mu)
      INTO v_provenance_id, v_initial_mu
      FROM substrate.provenance
     WHERE code = p_provenance_code;
    IF v_provenance_id IS NULL THEN
        RAISE EXCEPTION 'unknown provenance code: %', p_provenance_code;
    END IF;

    SELECT id INTO v_codepoint_etype
      FROM substrate.entity_type WHERE code = 'codepoint';
    IF v_codepoint_etype IS NULL THEN
        RAISE EXCEPTION 'entity_type code=''codepoint'' missing — bootstrap not applied?';
    END IF;

    SELECT id INTO v_s3_phys_type
      FROM substrate.physicality_type WHERE code = 's3_position';
    IF v_s3_phys_type IS NULL THEN
        RAISE EXCEPTION 'physicality_type code=''s3_position'' missing — bootstrap not applied?';
    END IF;

    SELECT id INTO v_source_auth_ctx
      FROM substrate.significance_context WHERE code = 'source_authority';
    IF v_source_auth_ctx IS NULL THEN
        RAISE EXCEPTION 'significance_context code=''source_authority'' missing — bootstrap not applied?';
    END IF;

    -- 1. Insert all 1,114,112 codepoint entities.
    INSERT INTO substrate.entity (hash)
    SELECT substrate.cp_hash(gs.cp)
      FROM generate_series(0, 1114111) AS gs(cp)
    ON CONFLICT (hash) DO NOTHING;

    -- 2. Classify each as 'codepoint' under the given provenance.
    INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
    SELECT substrate.cp_hash(gs.cp), v_codepoint_etype, v_provenance_id
      FROM generate_series(0, 1114111) AS gs(cp)
    ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING;

    -- 3. S^3 physicality built from scalar axis accessors.
    INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom)
    SELECT v_s3_phys_type,
           substrate.cp_hash(gs.cp),
           substrate.cp_hash(gs.cp),
           ST_MakePoint(
               substrate.cp_x(gs.cp),
               substrate.cp_y(gs.cp),
               substrate.cp_z(gs.cp),
               substrate.cp_m(gs.cp)
           )
      FROM generate_series(0, 1114111) AS gs(cp)
    ON CONFLICT DO NOTHING;

    -- 4. Source-authority significance prior.
    INSERT INTO substrate.entity_significance (context_type_id, entity_hash, mu, sigma, volatility, games)
    SELECT v_source_auth_ctx,
           substrate.cp_hash(gs.cp),
           v_initial_mu,
           350.0,
           0.06,
           0
      FROM generate_series(0, 1114111) AS gs(cp)
    ON CONFLICT DO NOTHING;

    RETURN 1114112;
END $$;

COMMENT ON FUNCTION substrate.populate_codepoint_atoms(TEXT, FLOAT8) IS
  'Bulk-fill substrate.entity + entity_classification + physicality(s3_position) + entity_significance(source_authority) for all 1,114,112 codepoints from the hartonomous extension''s embedded UCD 17.0.0 tables using set-based INSERT...SELECT over generate_series. Idempotent via ON CONFLICT.';
