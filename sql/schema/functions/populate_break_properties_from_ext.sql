-- substrate.populate_break_properties_from_ext()
--
-- Drives substrate.break_property from the embedded UCD catalog. The
-- inventory SETOF returns (id, category, code, enum_id) where category
-- is the UAX #29 category (GCB/WB/SB/LB/InCB). Reference table IDs are
-- pinned to extension_id + 1 so high-volume codepoint_property loading can
-- project FK IDs directly without per-row reference joins.
--
-- Idempotent on the deterministic ID. A conflicting (code, category) at
-- another ID is a data-corruption signal, not something to silently merge.

CREATE OR REPLACE FUNCTION substrate.populate_break_properties_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    -- enum_id: per-category enum value (UC_GCB_Other = 0, UC_GCB_CR = 1, …,
    -- UC_WB_Other = 0, UC_WB_CR = 1, …). codepoint_property INSERTs JOIN on
    -- (category, enum_id) so seed reorder / new categories don't break the
    -- mapping the way the prior offset arithmetic (a.gcb + 1, a.wb + 15,
    -- a.sb + 35, a.lb + 50) did when GCB count shifted.
    INSERT INTO substrate.break_property (id, code, category, enum_id)
    SELECT v.id + 1, v.code, v.category, v.enum_id
    FROM substrate.ucd_break_properties() AS v
    ON CONFLICT (id) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;

    PERFORM setval(pg_get_serial_sequence('substrate.break_property', 'id'),
                   (SELECT max(id) FROM substrate.break_property), true);

    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_break_properties_from_ext() IS
    'Bulk-loads substrate.break_property with id = extension_id + 1 plus per-category enum_id. Each row is a (category, code, enum_id) tuple — GCB/WB/SB/LB/InCB enums tagged at generation time. enum_id matches the UC_<category>_<code> #define in pg_ucd_segmentation.h. Idempotent.';
