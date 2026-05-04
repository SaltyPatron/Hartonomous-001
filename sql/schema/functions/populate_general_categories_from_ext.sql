-- substrate.populate_general_categories_from_ext()
--
-- Drives substrate.general_category from the embedded UCD catalog. The
-- inventory SETOF carries (id, code, description, group_code) directly
-- from pg_unicode_inventory.c — no derivation needed.
--
-- Idempotent — ON CONFLICT (code) DO NOTHING.

CREATE OR REPLACE FUNCTION substrate.populate_general_categories_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.general_category (code, group_code, description)
    SELECT v.code, v.group_code, v.description
    FROM substrate.ucd_general_categories() AS v
    ON CONFLICT (code) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_general_categories_from_ext() IS
    'Bulk-loads substrate.general_category from the embedded UCD catalog. Idempotent. Returns the number of rows inserted on this call.';
