-- 0025 down — Remove codepoint_value column from codepoint_property.

DROP INDEX IF EXISTS substrate.idx_codepoint_property_value;
ALTER TABLE substrate.codepoint_property DROP COLUMN IF EXISTS codepoint_value;
