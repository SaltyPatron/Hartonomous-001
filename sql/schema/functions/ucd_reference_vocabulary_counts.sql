CREATE OR REPLACE FUNCTION substrate.ucd_reference_vocabulary_counts()
RETURNS TABLE (
    general_category_rows BIGINT,
    script_rows           BIGINT,
    block_rows            BIGINT,
    bidi_class_rows       BIGINT,
    east_asian_width_rows BIGINT,
    break_property_rows   BIGINT
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT
        (SELECT count(*) FROM substrate.general_category)  AS general_category_rows,
        (SELECT count(*) FROM substrate.script)            AS script_rows,
        (SELECT count(*) FROM substrate.block)             AS block_rows,
        (SELECT count(*) FROM substrate.bidi_class)        AS bidi_class_rows,
        (SELECT count(*) FROM substrate.east_asian_width)  AS east_asian_width_rows,
        (SELECT count(*) FROM substrate.break_property)    AS break_property_rows;
$f$;

COMMENT ON FUNCTION substrate.ucd_reference_vocabulary_counts() IS
    'Single-row 6-column row-count probe for the UCD reference vocabularies. Used by UnicodeDecomposer §2 to verify seed presence before §3 codepoint atom emission relies on the +1 enum-code-to-id arithmetic.';
