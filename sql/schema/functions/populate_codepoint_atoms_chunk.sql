-- substrate.populate_codepoint_atoms_chunk(provenance_code, trust_mu, cp_lo, cp_hi)
--
-- Range-partitioned variant of populate_codepoint_atoms. Same semantics —
-- bulk-INSERT entity + entity_classification + physicality(s3_position) +
-- entity_significance(source_authority) for codepoints in [cp_lo, cp_hi).
-- The C# UCD seed orchestrator calls this N times concurrently with disjoint
-- ranges, putting N PG backends on the work in parallel instead of one
-- backend processing all 1,114,112 rows sequentially.
--
-- Determinism (Law #6): substrate.ucd_codepoints(cp_lo, cp_hi) is the same
-- SRF as ucd_codepoints() restricted to the requested range. Same UCD
-- version + same range → byte-identical substrate state across runs.
--
-- All resolve_*_id calls are hoisted ONCE per chunk (not per row).
CREATE OR REPLACE FUNCTION substrate.populate_codepoint_atoms_chunk(
    p_provenance_code TEXT,
    p_trust_mu        FLOAT8,
    p_cp_lo           INT,
    p_cp_hi           INT
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
    v_count                BIGINT;
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

    v_attestation_type_id := substrate.resolve_attestation_type_id('positive_evidence');
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'attestation_type code=''positive_evidence'' missing — bootstrap not applied?';
    END IF;

    INSERT INTO substrate.entity (hash)
    SELECT a.hash
      FROM substrate.ucd_codepoints(p_cp_lo, p_cp_hi) a
    ON CONFLICT (hash) DO NOTHING;

    INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
    SELECT a.hash, v_codepoint_etype, v_provenance_id
      FROM substrate.ucd_codepoints(p_cp_lo, p_cp_hi) a
    ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING;

    INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom)
    SELECT v_s3_phys_type,
           a.hash,
           a.hash,
           ST_MakePoint(a.x, a.y, a.z, a.m)
      FROM substrate.ucd_codepoints(p_cp_lo, p_cp_hi) a
    ON CONFLICT DO NOTHING;

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
      FROM substrate.ucd_codepoints(p_cp_lo, p_cp_hi) a
    ON CONFLICT DO NOTHING;

    v_count := p_cp_hi - p_cp_lo;
    RETURN v_count;
END $$;

COMMENT ON FUNCTION substrate.populate_codepoint_atoms_chunk(TEXT, FLOAT8, INT, INT) IS
    'Range-partitioned codepoint atom seed. Use with N concurrent C# tasks to spread the 1,114,112-row UCD seed across N PG backends. Each call processes [p_cp_lo, p_cp_hi). resolve_*_id calls hoisted once per chunk.';
