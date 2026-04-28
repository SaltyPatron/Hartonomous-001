-- 0015_expand_significance_range.down.sql
-- Revert to original 0-3000 range.

UPDATE substrate.provenance SET initial_mu = CASE code
    WHEN 'unicode_consortium'    THEN 2000.0
    WHEN 'sil_international'     THEN 2000.0
    WHEN 'princeton_wordnet'     THEN 1800.0
    WHEN 'omwn_consortium'       THEN 1600.0
    WHEN 'universaldependencies' THEN 1600.0
    WHEN 'huggingface_model'     THEN 1500.0
    WHEN 'wiktextract'           THEN 1400.0
    WHEN 'system_computed'       THEN 1300.0
    WHEN 'tatoeba'               THEN 1200.0
    WHEN 'user_session'          THEN 1000.0
END;

ALTER TABLE substrate.entity_pos ALTER COLUMN mu SET DEFAULT 1500;
ALTER TABLE substrate.entity_sense ALTER COLUMN mu SET DEFAULT 1500;
ALTER TABLE substrate.pattern_deprel ALTER COLUMN mu SET DEFAULT 1200;

ALTER TABLE substrate.entity_pos ALTER COLUMN sigma SET DEFAULT 350;
ALTER TABLE substrate.entity_sense ALTER COLUMN sigma SET DEFAULT 350;
ALTER TABLE substrate.pattern_deprel ALTER COLUMN sigma SET DEFAULT 350;

ALTER TABLE substrate.significance ALTER COLUMN mu SET DEFAULT 1500.0;
ALTER TABLE substrate.significance ALTER COLUMN sigma SET DEFAULT 350.0;

COMMENT ON DOMAIN substrate.significance_mu IS
    'Glicko-2 rating mean. Typical range 0-3000, trust priors 1000-2000.';
