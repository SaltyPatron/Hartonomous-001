-- Populate a bounded codepoint_property slice from the embedded UCD catalog.
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
    v_slice_count INT := GREATEST(0, LEAST(COALESCE(p_count, 0), 1114112 - GREATEST(0, LEAST(COALESCE(p_start, 0), 1114112))));
    v_end INT := v_slice_start + v_slice_count;
    v_lo INT := v_slice_start;
    v_hi INT;
    v_inserted INT;
    v_total INT := 0;
    -- Per-INSERT chunk small enough that the AfterTriggerEnd FK validation
    -- queue (7 FK columns × chunk_size rows × per-event tuple slot) does
    -- not exhaust the per-query memory context and SIGSEGV
    -- MakeTupleTableSlot. 32768 was the prior value; it crashed PG 18.3
    -- with ~224k queued FK events per query. 1024 keeps the queue under
    -- ~7k events — well below any memory ceiling. The seed driver still
    -- chunks at 32768 client-side; this caps the SQL function's INTERNAL
    -- per-INSERT batch independently.
    v_max_srf_rows CONSTANT INT := 1024;
BEGIN
    WHILE v_lo < v_end LOOP
        v_hi := LEAST(v_lo + v_max_srf_rows, v_end);

        -- FK IDs resolved via JOIN against (category, enum_id) instead of
        -- the prior offset arithmetic (a.gcb + 1, a.wb + 15, a.sb + 35,
        -- a.lb + 50). The offsets assumed a specific contiguous layout in
        -- substrate.break_property; when UCD enum counts shifted, the
        -- resulting INSERTs referenced non-existent FK IDs and PG 18.3's
        -- RI_FKey_check trigger SIGSEGV'd in get_op_opfamily_properties /
        -- syscache GETSTRUCT instead of returning a clean FK violation
        -- (2026-05-08, core wsl-crash-1778290623-2791).
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
            FROM substrate.ucd_codepoints(v_lo, v_hi - v_lo) a
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

        v_total := v_total + v_inserted;
        v_lo := v_hi;
    END LOOP;

    RETURN v_total;
END;
$$;

COMMENT ON FUNCTION substrate.populate_codepoint_property_range_from_ext(INT, INT) IS
    'Populates a bounded codepoint_property slice from the embedded UCD catalog. Internally caps native SRF scans at 32,768 rows; seed callers provide client-side chunk boundaries.';