-- Populate a bounded codepoint_property slice from the embedded UCD catalog.
--
-- One INSERT per call. plpgsql wrapper, no internal loop. The C# driver
-- chunks the 1,114,112-codepoint range at 32,768 cp per call.
--
-- An earlier rewrite to LANGUAGE sql made the SEGV worse — PG inlines
-- LANGUAGE sql function bodies into the caller's plan, which here forced
-- the SRF + INSERT to execute directly in the driver's connection scope.
-- That moved the crash earlier in the chunked seed (chunk 11 vs chunk 28).
-- plpgsql gives the function body its own statement-level execution scope
-- so a per-call problem doesn't poison the connection.
--
-- The actual SEGV root cause is in the C extension's UCD blob mmap layer
-- (ucd_atoms_blob.c) — see that file's heap-copy defensive fix.
--
-- break_property FK IDs resolved via JOIN against (category, enum_id).
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
    'Populates a bounded codepoint_property slice from the embedded UCD catalog in one set-based INSERT-SELECT inside a plpgsql wrapper (LANGUAGE sql inlines into the caller plan and moves the SEGV envelope earlier; plpgsql gives the body its own scope). The actual SEGV root cause is in ucd_atoms_blob.c mmap pointers — see the heap-copy defensive fix there.';
