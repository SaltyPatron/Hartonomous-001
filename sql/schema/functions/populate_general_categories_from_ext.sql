-- substrate.populate_general_categories_from_ext()
--
-- Drives substrate.general_category from the embedded UCD catalog. The
-- inventory SETOF carries (id, code, description, group_code) directly
-- from pg_unicode_inventory.c. Reference table IDs are pinned to
-- extension_id + 1 so high-volume codepoint_property loading can project FK
-- IDs directly without per-row reference joins.
--
-- Idempotent on the deterministic ID. A conflicting code at another ID is a
-- data-corruption signal, not something to silently merge.

CREATE OR REPLACE FUNCTION substrate.populate_general_categories_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.general_category (id, code, group_code, description)
    SELECT v.id + 1, v.code, v.group_code, v.description
    FROM substrate.ucd_general_categories() AS v
    ON CONFLICT (id) DO NOTHING;

    PERFORM setval(pg_get_serial_sequence('substrate.general_category', 'id'),
                   (SELECT max(id) FROM substrate.general_category), true);

    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_general_categories_from_ext() IS
    'Bulk-loads substrate.general_category from the embedded UCD catalog with id = extension_id + 1. Idempotent. Returns the number of rows inserted on this call.';
