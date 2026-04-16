-- Migration 0002: Domains
-- Per specs/sql/domains-and-types.md.

CREATE DOMAIN substrate.hash_value AS BYTEA
    CONSTRAINT hash_value_length CHECK (octet_length(VALUE) = 32);
COMMENT ON DOMAIN substrate.hash_value IS
    'BLAKE3 256-bit hash. Used for entity.hash and edge.hash.';

CREATE DOMAIN substrate.significance_mu AS FLOAT8;
COMMENT ON DOMAIN substrate.significance_mu IS
    'Glicko-2 rating mean. Typical range 0-3000, trust priors 1000-2000.';

CREATE DOMAIN substrate.significance_sigma AS FLOAT8
    CONSTRAINT sigma_positive CHECK (VALUE > 0);
COMMENT ON DOMAIN substrate.significance_sigma IS
    'Glicko-2 rating uncertainty. Decreases as evidence accumulates. Must be > 0.';

CREATE DOMAIN substrate.significance_volatility AS FLOAT8
    CONSTRAINT volatility_positive CHECK (VALUE > 0);
COMMENT ON DOMAIN substrate.significance_volatility IS
    'Glicko-2 meta-uncertainty. Must be > 0.';

CREATE DOMAIN substrate.tier_number AS INTEGER
    CONSTRAINT tier_non_negative CHECK (VALUE >= 0);
COMMENT ON DOMAIN substrate.tier_number IS
    'Entity tier. 0 = atom (codepoint). Emergent from reference depth.';

CREATE DOMAIN substrate.rle_count AS INTEGER
    CONSTRAINT rle_at_least_one CHECK (VALUE >= 1);
COMMENT ON DOMAIN substrate.rle_count IS
    'RLE occurrence count in sequence.';

CREATE DOMAIN substrate.ordinal_position AS INTEGER
    CONSTRAINT position_non_negative CHECK (VALUE >= 0);
COMMENT ON DOMAIN substrate.ordinal_position IS
    '0-indexed ordinal position in a parent composition.';

CREATE DOMAIN substrate.code_value AS VARCHAR(128)
    CONSTRAINT code_not_empty CHECK (LENGTH(TRIM(VALUE)) > 0);
COMMENT ON DOMAIN substrate.code_value IS
    'Reference table code column. Never empty or whitespace-only.';
