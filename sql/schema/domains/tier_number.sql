CREATE DOMAIN substrate.tier_number AS INTEGER
    CONSTRAINT tier_non_negative CHECK (VALUE >= 0);
COMMENT ON DOMAIN substrate.tier_number IS
    'Composition tier. 0 = atom (codepoint, codeword, sample). Emergent from reference depth, not stored as a column.';
