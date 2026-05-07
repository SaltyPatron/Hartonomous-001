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
    v_max_srf_rows CONSTANT INT := 32768;
BEGIN
    WHILE v_lo < v_end LOOP
        v_hi := LEAST(v_lo + v_max_srf_rows, v_end);

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
                a.gcb + 1,
                a.wb + 15,
                a.sb + 35,
                a.lb + 50,
                a.extended_pictographic,
                a.ccc::SMALLINT,
                a.decomposition_mapping,
                NULLIF(a.simple_case_fold, -1),
                a.full_case_fold
            FROM substrate.ucd_codepoints(v_lo, v_hi - v_lo) a
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