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
    SELECT
        cp.codepoint_value,
        cp.gcb_id,
        cp.wb_id,
        cp.sb_id,
        cp.lb_id,
        cp.is_extended_pictographic,
        cp.simple_case_fold,
        cp.full_case_fold
      FROM substrate.codepoint_property cp
     WHERE p_codepoints IS NULL
        OR cp.codepoint_value = ANY(p_codepoints)
     ORDER BY cp.codepoint_value;
$f$;

COMMENT ON FUNCTION substrate.codepoint_property_rows(INT[]) IS
    'Return codepoint_property rows for either all codepoints or an explicit requested working set.';