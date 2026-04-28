CREATE DOMAIN substrate.ordinal_position AS INTEGER
    CONSTRAINT position_non_negative CHECK (VALUE >= 0);
COMMENT ON DOMAIN substrate.ordinal_position IS
    '0-indexed ordinal position in a parent composition (substrate.sequence).';
