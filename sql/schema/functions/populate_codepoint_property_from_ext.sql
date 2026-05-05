-- substrate.populate_codepoint_property_from_ext()
--
-- Bulk-populates substrate.codepoint_property from the embedded UCD
-- catalog. Replaces the C# UCD decomposer's per-codepoint round-trips
-- with one set-based INSERT over generate_series(0, 1114111), using
-- scalar cp_* extension accessors and FK lookup tables for enum-id
-- translation.
--
-- The reference tables MUST already be populated. Call order in
-- bootstrap.sql / scripts/seed/Ucd.ps1:
--   1. populate_general_categories_from_ext()
--   2. populate_scripts_from_ext()
--   3. populate_blocks_from_ext()
--   4. populate_break_properties_from_ext()
--   5. populate_codepoint_property_from_ext()
--
-- Reference-table FK translation: the embedded catalog's enum ids are
-- 0-based array indices; substrate reference tables use 1-based SERIAL ids.
-- We pre-build small temp lookup tables joining the inventory SRFs to
-- the reference tables on (code) / (code, category) so the bulk SELECT
-- stays planar.
--
-- Idempotent — ON CONFLICT (entity_hash) DO NOTHING.

CREATE OR REPLACE FUNCTION substrate.populate_codepoint_property_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
    v_rows int;
    v_lo int := 0;
    v_hi int;
BEGIN
    CREATE TEMP TABLE IF NOT EXISTS _gc_lookup ON COMMIT DROP AS
        SELECT v.id AS ext_id, gc.id AS ref_id
        FROM substrate.ucd_general_categories() v
        JOIN substrate.general_category gc ON gc.code = v.code;
    CREATE TEMP TABLE IF NOT EXISTS _script_lookup ON COMMIT DROP AS
        SELECT v.id AS ext_id, s.id AS ref_id
        FROM substrate.ucd_scripts() v
        JOIN substrate.script s ON s.code = v.code;
    CREATE TEMP TABLE IF NOT EXISTS _block_lookup ON COMMIT DROP AS
        SELECT v.id AS ext_id, b.id AS ref_id
        FROM substrate.ucd_blocks() v
        JOIN substrate.block b ON b.code = v.code;
    -- Break-property lookups split by category; each row in the embedded
    -- inventory has explicit category, so we filter on it directly. The
    -- enum_id field in the inventory is the per-category small-int that
    -- ucd_codepoints() returns in gcb/wb/sb/lb columns.
    CREATE TEMP TABLE IF NOT EXISTS _bp_lookup_gcb ON COMMIT DROP AS
        SELECT v.enum_id AS ext_id, bp.id AS ref_id
        FROM substrate.ucd_break_properties() v
        JOIN substrate.break_property bp ON bp.code = v.code AND bp.category = 'GCB'
        WHERE v.category = 'GCB';
    CREATE TEMP TABLE IF NOT EXISTS _bp_lookup_wb ON COMMIT DROP AS
        SELECT v.enum_id AS ext_id, bp.id AS ref_id
        FROM substrate.ucd_break_properties() v
        JOIN substrate.break_property bp ON bp.code = v.code AND bp.category = 'WB'
        WHERE v.category = 'WB';
    CREATE TEMP TABLE IF NOT EXISTS _bp_lookup_sb ON COMMIT DROP AS
        SELECT v.enum_id AS ext_id, bp.id AS ref_id
        FROM substrate.ucd_break_properties() v
        JOIN substrate.break_property bp ON bp.code = v.code AND bp.category = 'SB'
        WHERE v.category = 'SB';
    CREATE TEMP TABLE IF NOT EXISTS _bp_lookup_lb ON COMMIT DROP AS
        SELECT v.enum_id AS ext_id, bp.id AS ref_id
        FROM substrate.ucd_break_properties() v
        JOIN substrate.break_property bp ON bp.code = v.code AND bp.category = 'LB'
        WHERE v.category = 'LB';

    inserted := 0;

    WHILE v_lo < 1114112 LOOP
        v_hi := LEAST(v_lo + 200000, 1114112);

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
            substrate.cp_hash(gs.cp),
            gs.cp,
            gcl.ref_id,
            scrl.ref_id,
            blkl.ref_id,
            gbpl.ref_id,
            wbpl.ref_id,
            sbpl.ref_id,
            lbpl.ref_id,
            substrate.cp_extended_pictographic(gs.cp),
            substrate.cp_ccc(gs.cp)::SMALLINT,
            substrate.cp_decomp(gs.cp),
            NULLIF(substrate.cp_simple_case_fold(gs.cp), -1),
            substrate.cp_full_case_fold(gs.cp)
        FROM generate_series(v_lo, v_hi - 1) AS gs(cp)
        LEFT JOIN _gc_lookup       gcl  ON gcl.ext_id  = substrate.cp_general_category(gs.cp)
        LEFT JOIN _script_lookup   scrl ON scrl.ext_id = substrate.cp_script(gs.cp)
        LEFT JOIN _block_lookup    blkl ON blkl.ext_id = substrate.cp_block(gs.cp)
        LEFT JOIN _bp_lookup_gcb   gbpl ON gbpl.ext_id = substrate.cp_gcb(gs.cp)
        LEFT JOIN _bp_lookup_wb    wbpl ON wbpl.ext_id = substrate.cp_wb(gs.cp)
        LEFT JOIN _bp_lookup_sb    sbpl ON sbpl.ext_id = substrate.cp_sb(gs.cp)
        LEFT JOIN _bp_lookup_lb    lbpl ON lbpl.ext_id = substrate.cp_lb(gs.cp)
        WHERE gcl.ref_id IS NOT NULL
          AND scrl.ref_id IS NOT NULL
          AND blkl.ref_id IS NOT NULL
        ON CONFLICT (entity_hash) DO NOTHING;

        GET DIAGNOSTICS v_rows = ROW_COUNT;
        inserted := inserted + v_rows;
        v_lo := v_hi;
    END LOOP;

    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_codepoint_property_from_ext() IS
    'Bulk-populates substrate.codepoint_property from the embedded UCD catalog in a single set-based INSERT over generate_series(0,1114111) with scalar cp_* accessors. Reference tables (general_category, script, block, break_property) MUST already be populated. Idempotent.';
