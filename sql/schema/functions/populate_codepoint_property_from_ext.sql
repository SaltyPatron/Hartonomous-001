-- substrate.populate_codepoint_property_from_ext()
--
-- Bulk-populates substrate.codepoint_property from the embedded UCD
-- catalog. Replaces the C# UCD decomposer's per-codepoint round-trips
-- with a single C-driven scan: substrate.ucd_codepoints() emits all
-- 1.1M rows in one call; we JOIN to the reference tables (already
-- populated by populate_general_categories/scripts/blocks/break_properties)
-- to translate the embedded enum ids into FK ids.
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
BEGIN
    -- Warm up: force the substrate.codepoint_atom composite-type tupdesc to
    -- be resolved + cached BEFORE plpgsql plans the bulk INSERT below.
    PERFORM 1 FROM substrate.ucd_codepoints(0, 1);

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
        gcl.ref_id,
        scrl.ref_id,
        blkl.ref_id,
        gbpl.ref_id,
        wbpl.ref_id,
        sbpl.ref_id,
        lbpl.ref_id,
        a.extended_pictographic,
        a.ccc::SMALLINT,
        a.decomposition_mapping,
        NULLIF(a.simple_case_fold, -1),
        a.full_case_fold
    FROM substrate.ucd_codepoints() a
    LEFT JOIN _gc_lookup       gcl  ON gcl.ext_id  = a.general_category
    LEFT JOIN _script_lookup   scrl ON scrl.ext_id = a.script
    LEFT JOIN _block_lookup    blkl ON blkl.ext_id = a.block
    LEFT JOIN _bp_lookup_gcb   gbpl ON gbpl.ext_id = a.gcb
    LEFT JOIN _bp_lookup_wb    wbpl ON wbpl.ext_id = a.wb
    LEFT JOIN _bp_lookup_sb    sbpl ON sbpl.ext_id = a.sb
    LEFT JOIN _bp_lookup_lb    lbpl ON lbpl.ext_id = a.lb
    WHERE gcl.ref_id IS NOT NULL
      AND scrl.ref_id IS NOT NULL
      AND blkl.ref_id IS NOT NULL
    ON CONFLICT (entity_hash) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_codepoint_property_from_ext() IS
    'Bulk-populates substrate.codepoint_property from the embedded UCD catalog in a single SQL statement via substrate.ucd_codepoints(). Reference tables (general_category, script, block, break_property) MUST already be populated. Idempotent.';
