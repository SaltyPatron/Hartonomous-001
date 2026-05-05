-- substrate.populate_break_properties_from_ext()
--
-- Drives substrate.break_property from the embedded UCD catalog. The
-- inventory SETOF returns (id, category, code, enum_id) where category
-- is the UAX #29 category (GCB/WB/SB/LB/InCB). Reference table IDs are
-- pinned to extension_id + 1 so high-volume codepoint_property loading can
-- project FK IDs directly without per-row reference joins.
--
-- Idempotent — ON CONFLICT (code, category) DO NOTHING.

CREATE OR REPLACE FUNCTION substrate.populate_break_properties_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.break_property (id, code, category)
    SELECT v.id + 1, v.code, v.category
    FROM substrate.ucd_break_properties() AS v
    ON CONFLICT (id) DO NOTHING;

    PERFORM setval(pg_get_serial_sequence('substrate.break_property', 'id'),
                   (SELECT max(id) FROM substrate.break_property), true);

    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_break_properties_from_ext() IS
    'Bulk-loads substrate.break_property with id = extension_id + 1. Each row is a (category, code) pair — GCB/WB/SB/LB/InCB enums tagged at generation time. Idempotent.';
