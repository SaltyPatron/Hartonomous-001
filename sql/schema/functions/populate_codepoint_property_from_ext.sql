-- substrate.populate_codepoint_property_from_ext()
--
-- Bulk-populates substrate.codepoint_property from the embedded UCD
-- catalog. Replaces the C# UCD decomposer's per-codepoint round-trips
-- with chunked C-driven scans: substrate.ucd_codepoints(lo, count) emits
-- bounded slices; we JOIN to the reference tables (already populated by
-- populate_general_categories/scripts/blocks/break_properties) to translate
-- the embedded enum ids into FK ids.
--
-- The reference tables MUST already be populated. Call order in
-- scripts/seed/Ucd.ps1:
--   1. populate_general_categories_from_ext()
--   2. populate_scripts_from_ext()
--   3. populate_blocks_from_ext()
--   4. populate_break_properties_from_ext()
--   5. populate_codepoint_property_range_from_ext(lo, count), invoked from
--      the seed script in separate client-side chunks.
--
-- Reference-table FK translation: the embedded catalog's enum ids are
-- 0-based. The UCD reference loaders pin substrate reference IDs to
-- extension_id + 1, so this hot path projects FK IDs directly and lets the
-- table's FK constraints validate them. Break-property category offsets are
-- fixed by substrate.ucd_break_properties(): GCB 0→1, WB 0→15, SB 0→35,
-- LB 0→50. InCB exists in the reference table but codepoint_property does
-- not store it.
--
-- Idempotent — ON CONFLICT (entity_hash) DO NOTHING. The range function is
-- the real bulk-load primitive. Seed scripts call it from separate client-side
-- chunks so every chunk has its own statement/transaction boundary. Keeping
-- the batching boundary outside PL/pgSQL avoids a single backend accumulating
-- executor state for all 1.1M rows.

CREATE OR REPLACE FUNCTION substrate.populate_codepoint_property_range_from_ext(
    p_start INT,
    p_count INT
)
RETURNS int
LANGUAGE sql
VOLATILE
AS $$
    WITH
    args AS (
        SELECT
            GREATEST(0, LEAST(COALESCE(p_start, 0), 1114112)) AS slice_start,
            GREATEST(
                0,
                LEAST(
                    COALESCE(p_count, 0),
                    1114112 - GREATEST(0, LEAST(COALESCE(p_start, 0), 1114112))
                )
            ) AS slice_count
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
        a.gcb + 1,
        a.wb + 15,
        a.sb + 35,
        a.lb + 50,
        a.extended_pictographic,
        a.ccc::SMALLINT,
        a.decomposition_mapping,
        NULLIF(a.simple_case_fold, -1),
        a.full_case_fold
    FROM args
    CROSS JOIN LATERAL substrate.ucd_codepoints(args.slice_start, args.slice_count) a
        ON CONFLICT (entity_hash) DO NOTHING
        RETURNING 1
        )
        SELECT count(*)::int FROM inserted;
$$;

COMMENT ON FUNCTION substrate.populate_codepoint_property_range_from_ext(INT, INT) IS
    'Populates a bounded codepoint_property slice from the embedded UCD catalog. Intended seed primitive; callers provide client-side chunk boundaries so each chunk has a separate statement/transaction boundary.';

CREATE OR REPLACE FUNCTION substrate.populate_codepoint_property_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'populate_codepoint_property_from_ext() is intentionally disabled for the full UCD load; call populate_codepoint_property_range_from_ext(start,count) from the seed script so each chunk has a real client-side statement boundary';
END;
$$;

COMMENT ON FUNCTION substrate.populate_codepoint_property_from_ext() IS
    'Disabled compatibility wrapper. Use populate_codepoint_property_range_from_ext(start,count) from client-side chunks so each bounded insert has a real statement/transaction boundary.';
