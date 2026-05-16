-- substrate.resolve_attestation_type_id(p_code TEXT)
--
-- Translate an attestation_type code to its INT id. After the P1d collapse
-- the vocabulary is exactly three rows — positive_evidence,
-- negative_evidence, neutral_evidence. Unknown codes raise. The SQL +
-- C# emission sites have been migrated to use the canonical three-row
-- vocabulary; a hard-fail here surfaces any regression instead of silently
-- recoding evidence under a default sign.
--
-- Returns the resolved id; raises EXCEPTION on unknown code.
CREATE OR REPLACE FUNCTION substrate.resolve_attestation_type_id(p_code TEXT)
RETURNS INT
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    v_id INT;
BEGIN
    SELECT id INTO v_id FROM substrate.attestation_type WHERE code = p_code;
    IF v_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type code: % (expected positive_evidence / negative_evidence / neutral_evidence per P1d)', p_code;
    END IF;
    RETURN v_id;
END;
$$;

COMMENT ON FUNCTION substrate.resolve_attestation_type_id(TEXT) IS
    'Resolve an attestation_type.code to its INT id. Raises EXCEPTION on unknown code — the substrate''s 3-row vocabulary (positive_evidence / negative_evidence / neutral_evidence per P1d) is the only valid input. No graceful fallback (anti-band-aid).';
