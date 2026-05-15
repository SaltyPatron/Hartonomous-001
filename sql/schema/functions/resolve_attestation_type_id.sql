-- substrate.resolve_attestation_type_id(p_code TEXT)
--
-- Translate an attestation_type code to its INT id. P1d (2026-05-14): the
-- attestation_type vocabulary was collapsed from 27 modality-specific rows
-- to 3 generic sign-discriminator rows (positive_evidence /
-- negative_evidence / neutral_evidence). The (provenance × arena) tuple
-- carries source + domain discrimination instead.
--
-- Unknown codes (legacy modality-specific codes from pre-P1d decomposer
-- code that hasn't migrated yet — model_attention_qk_pattern,
-- corpus_co_occurrence_window, provenance_authority_corroboration, etc.)
-- resolve to 'positive_evidence' as a graceful fallback so the substrate
-- keeps ingesting while the call-site migration to the unified surface
-- (P1e) proceeds.
--
-- Returns the resolved id; never NULL post-P1d.
CREATE OR REPLACE FUNCTION substrate.resolve_attestation_type_id(p_code TEXT)
RETURNS INT
LANGUAGE sql STABLE
AS $$
    SELECT COALESCE(
        (SELECT id FROM substrate.attestation_type WHERE code = p_code),
        (SELECT id FROM substrate.attestation_type WHERE code = 'positive_evidence')
    );
$$;

COMMENT ON FUNCTION substrate.resolve_attestation_type_id(TEXT) IS
    'Resolve an attestation_type.code to its INT id. Falls back to positive_evidence for legacy modality-specific codes that pre-date the P1d collapse to generic sign discriminators (positive_evidence / negative_evidence / neutral_evidence). STABLE — safe to inline in larger queries.';
