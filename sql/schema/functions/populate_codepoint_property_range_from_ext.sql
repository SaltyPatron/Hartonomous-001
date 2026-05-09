-- Populate a bounded codepoint_property slice from the embedded UCD catalog.
--
-- One INSERT per call. NO internal WHILE loop. The client driver chunks the
-- 1,114,112-codepoint range at 32,768 cp per call; this function does each
-- of those chunks in a single set-based INSERT-SELECT.
--
-- Why no internal loop: plpgsql caches the SPI plan + ParamListInfo across
-- iterations of a WHILE body. After enough iterations within a single
-- backend the ParamListInfo's paramCompile function pointer is observed
-- corrupted to a heap address; the next ExecInitExprRec dispatch through
-- it (PG 18 execExpr.c:1061) executes non-X heap memory and the backend
-- SIGSEGVs. A single-statement function avoids cross-iteration param
-- caching entirely. Set-based INSERT with the SRF over the whole range is
-- already the right shape.
--
-- break_property FK IDs resolved via JOIN against (category, enum_id) so
-- shifting break_property seed counts don't break the mapping (the older
-- offset arithmetic a.gcb + 1, a.wb + 15, a.sb + 35, a.lb + 50 silently
-- desynchronised whenever the inventory's per-category counts shifted).
CREATE OR REPLACE FUNCTION substrate.populate_codepoint_property_range_from_ext(
    p_start INT,
    p_count INT
)
RETURNS int
LANGUAGE plpgsql
VOLATILE
AS $$
DECLARE
    v_slice_start INT := GREATEST(0, LEAST(COALESCE(p_start, 0), 1114112));
    v_slice_count INT := GREATEST(0, LEAST(COALESCE(p_count, 0), 1114112 - v_slice_start));
    v_inserted    INT;
BEGIN
    IF v_slice_count = 0 THEN
        RETURN 0;
    END IF;

    WITH inserted AS (
        INSERT INTO substrate.codepoint_property (
            entity_hash,
            codepoint_value,
            general_category_id,
            script_id,
            block_id,
            gcb_id, wb_id, sb_id, lb_id,
            is_extended_pictographic,
            ccc,
            decomposition_mapping,
            simple_case_fold,
            full_case_fold
        )
        SELECT
            a.hash,
            a.cp,
            a.general_category + 1,
            a.script + 1,
            a.block + 1,
            bp_gcb.id,
            bp_wb.id,
            bp_sb.id,
            bp_lb.id,
            a.extended_pictographic,
            a.ccc::SMALLINT,
            a.decomposition_mapping,
            NULLIF(a.simple_case_fold, -1),
            a.full_case_fold
        FROM substrate.ucd_codepoints(v_slice_start, v_slice_count) a
        JOIN substrate.break_property bp_gcb
          ON bp_gcb.category = 'GCB' AND bp_gcb.enum_id = a.gcb
        JOIN substrate.break_property bp_wb
          ON bp_wb.category  = 'WB'  AND bp_wb.enum_id  = a.wb
        JOIN substrate.break_property bp_sb
          ON bp_sb.category  = 'SB'  AND bp_sb.enum_id  = a.sb
        JOIN substrate.break_property bp_lb
          ON bp_lb.category  = 'LB'  AND bp_lb.enum_id  = a.lb
        ON CONFLICT (entity_hash) DO NOTHING
        RETURNING 1
    )
    SELECT count(*)::int INTO v_inserted FROM inserted;

    RETURN v_inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_codepoint_property_range_from_ext(INT, INT) IS
    'Populates a bounded codepoint_property slice from the embedded UCD catalog in one set-based INSERT-SELECT. No internal WHILE loop — the client driver already chunks the full range. break_property FK IDs resolved via JOIN on (category, enum_id) for self-correcting behaviour against seed reorders.';
