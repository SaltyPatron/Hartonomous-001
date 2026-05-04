-- substrate.populate_break_properties_from_ext()
--
-- Drives substrate.break_property from the embedded UCD catalog. The
-- inventory SETOF returns (id, category, code, enum_id) where category
-- is the UAX #29 category (GCB/WB/SB/LB) — no parsing required.
--
-- Idempotent — ON CONFLICT (code, category) DO NOTHING.

CREATE OR REPLACE FUNCTION substrate.populate_break_properties_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.break_property (code, category)
    SELECT v.code, v.category
    FROM substrate.ucd_break_properties() AS v
    ON CONFLICT (code, category) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_break_properties_from_ext() IS
    'Bulk-loads substrate.break_property from the embedded UCD catalog. Each row is a (category, code) pair — GCB/WB/SB/LB enums tagged at generation time. Idempotent.';
