-- Migration 0002 DOWN: Drop domains.

DROP DOMAIN IF EXISTS substrate.code_value;
DROP DOMAIN IF EXISTS substrate.ordinal_position;
DROP DOMAIN IF EXISTS substrate.rle_count;
DROP DOMAIN IF EXISTS substrate.tier_number;
DROP DOMAIN IF EXISTS substrate.significance_volatility;
DROP DOMAIN IF EXISTS substrate.significance_sigma;
DROP DOMAIN IF EXISTS substrate.significance_mu;
DROP DOMAIN IF EXISTS substrate.hash_value;
