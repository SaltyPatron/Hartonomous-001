CREATE DOMAIN substrate.rle_count AS INTEGER
    CONSTRAINT rle_at_least_one CHECK (VALUE >= 1);
COMMENT ON DOMAIN substrate.rle_count IS
    'Run-length count for repeated children at the same ordinal position in substrate.sequence.';
