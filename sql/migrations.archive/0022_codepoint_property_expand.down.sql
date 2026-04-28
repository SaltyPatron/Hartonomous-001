-- 0022 down — revert codepoint_property expansion.

DROP INDEX IF EXISTS substrate.idx_codepoint_property_has_casefold;
DROP INDEX IF EXISTS substrate.idx_codepoint_property_has_decomp;
DROP INDEX IF EXISTS substrate.idx_codepoint_property_ext_pict;

ALTER TABLE substrate.codepoint_property
    DROP CONSTRAINT IF EXISTS chk_codepoint_ccc_range;

ALTER TABLE substrate.codepoint_property
    DROP COLUMN IF EXISTS full_case_fold,
    DROP COLUMN IF EXISTS simple_case_fold,
    DROP COLUMN IF EXISTS decomposition_mapping,
    DROP COLUMN IF EXISTS decomposition_type,
    DROP COLUMN IF EXISTS ccc,
    DROP COLUMN IF EXISTS is_extended_pictographic;
