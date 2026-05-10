-- Populate a bounded codepoint_property slice from the embedded UCD catalog.
--
-- One INSERT per call. NO internal WHILE loop, NO plpgsql wrapper. The
-- client driver chunks the 1,114,112-codepoint range at 32,768 cp per call;
-- this function does each of those chunks in a single set-based INSERT-SELECT.
--
-- Why LANGUAGE sql, not plpgsql: a previous version was plpgsql-with-no-loop
-- (single INSERT inside DECLARE/BEGIN/END) on the theory that removing the
-- *inner* WHILE was sufficient to dodge plpgsql's ParamListInfo caching bug
-- (paramCompile function pointer corrupted to a heap address after enough
-- invocations within one backend; next ExecInitExprRec dispatch executes
-- non-X heap memory and SIGSEGVs — PG 18 execExpr.c:1061). It wasn't:
-- the SPI plan for the function's body is cached on the PLpgSQL_function
-- struct and reused across the *outer* 28 chunk calls the C# driver issues
-- on one connection. The same param-cache corruption resurfaces around
-- chunk 28 of the run with the same si_addr = small_int_in_heap | offset
-- signature. LANGUAGE sql functions do not cache through plpgsql at all
-- and inline at the call site, so this whole path is gone.
--
-- DECLARE clamps fold into a CTE.
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
LANGUAGE sql
VOLATILE
AS $$
    WITH bounds AS (
        SELECT
            GREATEST(0, LEAST(COALESCE(p_start, 0), 1114112))                                 AS v_start,
            GREATEST(0, LEAST(COALESCE(p_count, 0), 1114112 - GREATEST(0, LEAST(COALESCE(p_start, 0), 1114112)))) AS v_count
    ),
    inserted AS (
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
        FROM bounds b
        CROSS JOIN LATERAL substrate.ucd_codepoints(b.v_start, b.v_count) a
        JOIN substrate.break_property bp_gcb
          ON bp_gcb.category = 'GCB' AND bp_gcb.enum_id = a.gcb
        JOIN substrate.break_property bp_wb
          ON bp_wb.category  = 'WB'  AND bp_wb.enum_id  = a.wb
        JOIN substrate.break_property bp_sb
          ON bp_sb.category  = 'SB'  AND bp_sb.enum_id  = a.sb
        JOIN substrate.break_property bp_lb
          ON bp_lb.category  = 'LB'  AND bp_lb.enum_id  = a.lb
        WHERE b.v_count > 0
        ON CONFLICT (entity_hash) DO NOTHING
        RETURNING 1
    )
    SELECT count(*)::int FROM inserted;
$$;

COMMENT ON FUNCTION substrate.populate_codepoint_property_range_from_ext(INT, INT) IS
    'Populates a bounded codepoint_property slice from the embedded UCD catalog in one set-based INSERT-SELECT. LANGUAGE sql, not plpgsql — plpgsql''s SPI plan cache for the function body persists across the chunked outer driver calls and corrupts ParamListInfo (paramCompile pointer overwritten to a heap address) after enough invocations on one backend, SIGSEGVing in execExpr. break_property FK IDs resolved via JOIN on (category, enum_id) for self-correcting behaviour against seed reorders.';
