CREATE DOMAIN substrate.code_value AS VARCHAR(128)
    CONSTRAINT code_not_empty CHECK (LENGTH(TRIM(VALUE)) > 0);
COMMENT ON DOMAIN substrate.code_value IS
    'Reference table code column. Never empty or whitespace-only.';
