-- substrate.resolve_attestation_type_id(p_code TEXT)
--
-- Translate an attestation_type code to its INT id. Same shape as
-- resolve_context_id. AttestationType is open-vocabulary; new codes can be
-- added at runtime via INSERT. Code that hard-codes the 14 starter codes is
-- wrong (analogous to AP-1 for arenas).
--
-- Returns NULL when the code does not exist. Callers MUST handle NULL
-- (the C# pipeline raises InvalidOperationException with the unknown code).
CREATE OR REPLACE FUNCTION substrate.resolve_attestation_type_id(p_code TEXT)
RETURNS INT
LANGUAGE sql STABLE
AS $$
    SELECT id
      FROM substrate.attestation_type
     WHERE code = p_code;
$$;

COMMENT ON FUNCTION substrate.resolve_attestation_type_id(TEXT) IS
    'Resolve an attestation_type.code to its INT id. Returns NULL if unknown. STABLE — safe to inline in larger queries.';
