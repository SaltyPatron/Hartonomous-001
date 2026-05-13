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
-- IMPLEMENTATION NOTE — single SRF, zero per-row C calls.
--
-- The four bulk INSERTs all read from substrate.ucd_codepoints(), which
-- is a single C call returning all 1,114,112 rows with hash, x, y, z, m,
-- hilbert and every UCD property pre-computed. We do NOT call the scalar
-- substrate.cp_hash(cp) / cp_x(cp) / cp_y(cp) / cp_z(cp) / cp_m(cp)
-- accessors over generate_series — that is 5.6M scalar C invocations
-- per function call, which is fragile under executor pressure and
-- pointless when the SRF already materializes the same payload once.
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
    v_provenance_id        INT;
    v_codepoint_etype      INT;
    v_s3_phys_type         INT;
    v_source_auth_ctx      INT;
    v_attestation_type_id  INT;
    v_initial_mu           FLOAT8;
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

    -- Resolve attestation_type_id ONCE outside the SELECT below — invoking
    -- substrate.resolve_attestation_type_id() per row across 1.1M codepoints
    -- is gratuitous function-call overhead (single-threaded in one backend).
    v_attestation_type_id := substrate.resolve_attestation_type_id('provenance_authority_corroboration');
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'attestation_type code=''provenance_authority_corroboration'' missing — bootstrap not applied?';
    END IF;

    -- Warm up the composite tupdesc cache before plpgsql plans the SRF.
    PERFORM 1 FROM substrate.ucd_codepoints(0, 1);

    -- 1. Insert all 1,114,112 codepoint entities.
    INSERT INTO substrate.entity (hash)
    SELECT a.hash FROM substrate.ucd_codepoints() a
    ON CONFLICT (hash) DO NOTHING;

    -- 2. Classify each as 'codepoint' under the given provenance.
    INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
    SELECT a.hash, v_codepoint_etype, v_provenance_id
      FROM substrate.ucd_codepoints() a
    ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING;

    -- 3. S^3 physicality built from SRF-supplied (x,y,z,m).
    INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom)
    SELECT v_s3_phys_type,
           a.hash,
           a.hash,
           ST_MakePoint4D(a.x, a.y, a.z, a.m)
      FROM substrate.ucd_codepoints() a
    ON CONFLICT DO NOTHING;

    -- 4. Source-authority significance prior. UCD codepoint atoms come
    -- from the embedded Unicode 17.0.0 tables; the kind of evidence is
    -- provenance_authority_corroboration (Unicode Consortium asserts these
    -- codepoints exist with this initial mu).
    INSERT INTO substrate.entity_significance (
        context_type_id, entity_hash, attestation_type_id,
        mu, sigma, volatility, games)
    SELECT v_source_auth_ctx,
           a.hash,
           v_attestation_type_id,
           v_initial_mu,
           350.0,
           0.06,
           0
      FROM substrate.ucd_codepoints() a
    ON CONFLICT DO NOTHING;

    RETURN 1114112;
END $$;

COMMENT ON FUNCTION substrate.populate_codepoint_atoms(TEXT, FLOAT8) IS
  'Bulk-fill substrate.entity + entity_classification + physicality(s3_position) + entity_significance(source_authority) for all 1,114,112 codepoints from the hartonomous extension''s embedded UCD 17.0.0 tables using one SRF call (substrate.ucd_codepoints) per INSERT. Zero per-row scalar C invocations. Idempotent via ON CONFLICT.';
