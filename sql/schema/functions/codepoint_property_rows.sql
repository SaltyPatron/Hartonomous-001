-- Per-codepoint runtime property rows for the inference-path
-- NpgsqlCodepointPropertiesCache (text segmentation + case folding).
--
-- Gate 1 #38 refactor 2026-05-18: rewritten against the new narrow per-property
-- analytics caches (substrate.cp_grapheme_break / cp_word_break /
-- cp_sentence_break / cp_line_break) populated by UnicodeDecomposer §3.
-- The wide flat substrate.codepoint_property junction (deleted) is replaced
-- by typed has_cp_* edges on substrate.edge plus these narrow tables for
-- index-locality lookups.
--
-- Codepoint entity identity = BLAKE3 over the codepoint integer. The JOIN
-- reverse-resolves codepoint_value → entity hash via substrate.cp_hash(cp)
-- (C extension binding for hartonomous_blake3_codepoint).
--
-- The case-fold and is_extended_pictographic fields are NULL in this
-- function pending the case-fold narrow caches and extended_pictographic
-- table landing. Callers fall back to the embedded UCD blob via
-- BlobUcdPropertyAccessor.{SimpleCaseFold, FullCaseFold,
-- IsExtendedPictographic} — siblings per Principle 1.
CREATE OR REPLACE FUNCTION substrate.codepoint_property_rows(p_codepoints INT[] DEFAULT NULL)
RETURNS TABLE (
    codepoint_value INT,
    gcb_id INT,
    wb_id INT,
    sb_id INT,
    lb_id INT,
    is_extended_pictographic BOOLEAN,
    simple_case_fold INT,
    full_case_fold INT[]
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    WITH cps AS (
        SELECT u.cp::INT AS codepoint_value, substrate.cp_hash(u.cp::INT) AS entity_hash
          FROM unnest(COALESCE(p_codepoints, ARRAY(SELECT generate_series(0, 1114111)))) AS u(cp)
    )
    SELECT
        cps.codepoint_value,
        gcb.break_property_id  AS gcb_id,
        wb.break_property_id   AS wb_id,
        sb.break_property_id   AS sb_id,
        lb.break_property_id   AS lb_id,
        NULL::BOOLEAN          AS is_extended_pictographic,
        NULL::INT              AS simple_case_fold,
        NULL::INT[]            AS full_case_fold
      FROM cps
      LEFT JOIN substrate.cp_grapheme_break  gcb ON gcb.entity_hash = cps.entity_hash
      LEFT JOIN substrate.cp_word_break      wb  ON wb.entity_hash  = cps.entity_hash
      LEFT JOIN substrate.cp_sentence_break  sb  ON sb.entity_hash  = cps.entity_hash
      LEFT JOIN substrate.cp_line_break      lb  ON lb.entity_hash  = cps.entity_hash
     ORDER BY cps.codepoint_value;
$f$;

COMMENT ON FUNCTION substrate.codepoint_property_rows(INT[]) IS
    'Per-codepoint runtime properties from narrow per-property junctions. Gate 1 #38 refactor — case-fold and extended_pictographic fields are NULL pending narrow-cache landing; callers fall back to embedded UCD blob.';
