-- 0022 — Expand substrate.codepoint_property with the UCD fields needed for
-- UAX #29/14 segmentation, UAX #15 normalization, and UCD CaseFolding.
--
-- Adds:
--   is_extended_pictographic   boolean for UAX #29 GB11 (emoji ZWJ sequences)
--   ccc                        canonical combining class (0-255 per UCD)
--   decomposition_type         canonical | compatibility-type tag (e.g., 'can',
--                              'com', 'font', 'noBreak', 'initial', ...).
--                              NULL when the codepoint has no decomposition.
--   decomposition_mapping      INT[] of target codepoint scalars — canonical or
--                              compat decomposition per UnicodeData.txt field 5.
--                              NULL when none.
--   simple_case_fold           single target codepoint (status C + S of
--                              CaseFolding.txt). NULL when the codepoint folds
--                              to itself.
--   full_case_fold             INT[] of target codepoints (status C + F). One
--                              element when equal to simple fold, multiple for
--                              expansions like U+00DF → [0x73, 0x73]. NULL
--                              when the codepoint folds to itself.

ALTER TABLE substrate.codepoint_property
    ADD COLUMN is_extended_pictographic BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN ccc                      SMALLINT NOT NULL DEFAULT 0,
    ADD COLUMN decomposition_type       TEXT,
    ADD COLUMN decomposition_mapping    INT[],
    ADD COLUMN simple_case_fold         INT,
    ADD COLUMN full_case_fold           INT[];

ALTER TABLE substrate.codepoint_property
    ADD CONSTRAINT chk_codepoint_ccc_range CHECK (ccc BETWEEN 0 AND 255);

CREATE INDEX idx_codepoint_property_ext_pict
    ON substrate.codepoint_property(entity_id)
    WHERE is_extended_pictographic;

CREATE INDEX idx_codepoint_property_has_decomp
    ON substrate.codepoint_property(entity_id)
    WHERE decomposition_mapping IS NOT NULL;

CREATE INDEX idx_codepoint_property_has_casefold
    ON substrate.codepoint_property(entity_id)
    WHERE full_case_fold IS NOT NULL;

COMMENT ON COLUMN substrate.codepoint_property.is_extended_pictographic IS
    'UCD Extended_Pictographic. Drives UAX #29 GB11 emoji-ZWJ-sequence grouping.';
COMMENT ON COLUMN substrate.codepoint_property.ccc IS
    'UCD canonical combining class (0-255). Drives UAX #15 canonical reordering.';
COMMENT ON COLUMN substrate.codepoint_property.decomposition_type IS
    'UnicodeData.txt field 5 decomposition tag, e.g. ''can'' for canonical, ''com'' for compat types without a specific subtag.';
COMMENT ON COLUMN substrate.codepoint_property.decomposition_mapping IS
    'Target codepoints (UnicodeData.txt field 5). Canonical or compat mapping per decomposition_type.';
COMMENT ON COLUMN substrate.codepoint_property.simple_case_fold IS
    'CaseFolding.txt status C + S single-target fold. NULL when codepoint folds to itself.';
COMMENT ON COLUMN substrate.codepoint_property.full_case_fold IS
    'CaseFolding.txt status C + F multi-target fold. NULL when codepoint folds to itself.';
