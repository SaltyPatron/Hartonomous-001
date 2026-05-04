-- substrate.populate_blocks_from_ext()
--
-- Drives substrate.block from the embedded UCD catalog. range_start and
-- range_end come straight from pg_unicode_inventory.c — no aggregation
-- against the bulk codepoint SRF needed.
--
-- Idempotent — ON CONFLICT (code) DO NOTHING.

CREATE OR REPLACE FUNCTION substrate.populate_blocks_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.block (code, range_start, range_end)
    SELECT v.code, v.range_start, v.range_end
    FROM substrate.ucd_blocks() AS v
    ON CONFLICT (code) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_blocks_from_ext() IS
    'Bulk-loads substrate.block (with range_start/range_end direct from the embedded UCD catalog) — no aggregation pass over the codepoint SRF. Idempotent.';
