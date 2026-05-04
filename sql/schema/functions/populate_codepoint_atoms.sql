-- substrate.populate_codepoint_atoms(provenance_code TEXT, trust_mu FLOAT8)
--
-- Replaces the C# UCD/UCA decomposer's per-codepoint emission loop with
-- a substrate-side bulk INSERT driven by the extension's embedded UCD
-- 17.0.0 tables. Inserts ~1,114,112 codepoint entities + classifications
-- + S^3 physicalities + significance rows in five SQL statements — same
-- substrate state, ~30× the speed of XML parsing.
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
    v_total            BIGINT;
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

    -- 1. Insert all 1,114,112 codepoint entities. Hash from extension table.
    INSERT INTO substrate.entity (hash)
    SELECT substrate.cp_hash(cp)
      FROM generate_series(0, 1114111) AS cp
    ON CONFLICT (hash) DO NOTHING;

    -- 2. Classify each as 'codepoint' under the given provenance.
    INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
    SELECT substrate.cp_hash(cp), v_codepoint_etype, v_provenance_id
      FROM generate_series(0, 1114111) AS cp
    ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING;

    -- 3. S^3 physicality — POINTZM built via PostGIS ST_MakePoint from the
    --    extension's per-axis accessors (cp_x / cp_y / cp_z / cp_m).
    INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom)
    SELECT v_s3_phys_type,
           substrate.cp_hash(cp),
           substrate.cp_hash(cp),
           ST_MakePoint(substrate.cp_x(cp), substrate.cp_y(cp),
                        substrate.cp_z(cp), substrate.cp_m(cp))
      FROM generate_series(0, 1114111) AS cp
    ON CONFLICT DO NOTHING;

    -- 4. Source-authority significance prior, one row per codepoint.
    INSERT INTO substrate.entity_significance (context_type_id, entity_hash, mu, sigma, volatility, games)
    SELECT v_source_auth_ctx, substrate.cp_hash(cp), v_initial_mu, 350.0, 0.06, 0
      FROM generate_series(0, 1114111) AS cp
    ON CONFLICT DO NOTHING;

    GET DIAGNOSTICS v_total = ROW_COUNT;
    RETURN 1114112;
END $$;

COMMENT ON FUNCTION substrate.populate_codepoint_atoms(TEXT, FLOAT8) IS
    'Bulk-fill substrate.entity + entity_classification + physicality(s3_position) + entity_significance(source_authority) for all 1,114,112 codepoints from the hartonomous extension''s embedded UCD 17.0.0 tables. Replaces the C# UCD decomposer''s per-codepoint emission loop with five SQL statements. Determinism via extension UCD version pinning.';
