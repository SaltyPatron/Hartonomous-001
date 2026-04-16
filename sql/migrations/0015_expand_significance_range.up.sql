-- 0015_expand_significance_range.up.sql
-- Rescale Glicko-2 significance from 0-3000 range to 0-100000 range.
-- Factor: 100000/3000 ≈ 33.33x. Round trust priors to clean values.
-- Glicko-2 is scale-independent — formula is dimensionless.

-- Update trust priors in provenance table.
UPDATE substrate.provenance SET initial_mu = CASE code
    WHEN 'unicode_consortium'    THEN 95000.0  -- authoritative_standard (was 2000)
    WHEN 'sil_international'     THEN 95000.0  -- authoritative_standard (was 2000)
    WHEN 'princeton_wordnet'     THEN 85000.0  -- academic_curated (was 1800)
    WHEN 'omwn_consortium'       THEN 75000.0  -- academic_consortium (was 1600)
    WHEN 'universaldependencies' THEN 75000.0  -- academic_consortium (was 1600)
    WHEN 'huggingface_model'     THEN 70000.0  -- model_derived (was 1500)
    WHEN 'wiktextract'           THEN 60000.0  -- community_curated (was 1400)
    WHEN 'system_computed'       THEN 55000.0  -- system_computed (was 1300)
    WHEN 'tatoeba'               THEN 50000.0  -- community_contributed (was 1200)
    WHEN 'user_session'          THEN 40000.0  -- user_input (was 1000)
END;

-- Update default mu in junction tables.
ALTER TABLE substrate.entity_pos ALTER COLUMN mu SET DEFAULT 50000.0;
ALTER TABLE substrate.entity_sense ALTER COLUMN mu SET DEFAULT 50000.0;
ALTER TABLE substrate.pattern_deprel ALTER COLUMN mu SET DEFAULT 50000.0;

-- Update default sigma proportionally (350/1500 ≈ 23.3% → 23.3% of 50000 ≈ 11667).
ALTER TABLE substrate.entity_pos ALTER COLUMN sigma SET DEFAULT 11667.0;
ALTER TABLE substrate.entity_sense ALTER COLUMN sigma SET DEFAULT 11667.0;
ALTER TABLE substrate.pattern_deprel ALTER COLUMN sigma SET DEFAULT 11667.0;

-- Update default significance in core significance table.
ALTER TABLE substrate.significance ALTER COLUMN mu SET DEFAULT 50000.0;
ALTER TABLE substrate.significance ALTER COLUMN sigma SET DEFAULT 11667.0;

-- Update domain comment.
COMMENT ON DOMAIN substrate.significance_mu IS
    'Glicko-2 rating mean. Range 0-100000, trust priors 40000-95000.';
