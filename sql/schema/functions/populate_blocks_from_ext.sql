-- substrate.populate_blocks_from_ext()
--
-- Drives substrate.block from the embedded UCD catalog. range_start and
-- range_end come straight from pg_unicode_inventory.c — no aggregation
-- against the bulk codepoint SRF needed. Reference table IDs are pinned to
-- extension_id + 1 so high-volume codepoint_property loading can project FK
-- IDs directly without per-row reference joins.
--
-- Idempotent — ON CONFLICT (code) DO NOTHING.

CREATE OR REPLACE FUNCTION substrate.populate_blocks_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.block (id, code, range_start, range_end)
    SELECT v.id + 1, v.code, v.range_start, v.range_end
    FROM substrate.ucd_blocks() AS v
    ON CONFLICT (id) DO NOTHING;

    PERFORM setval(pg_get_serial_sequence('substrate.block', 'id'),
                   (SELECT max(id) FROM substrate.block), true);

    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_blocks_from_ext() IS
    'Bulk-loads substrate.block with id = extension_id + 1 and range_start/range_end direct from the embedded UCD catalog. No aggregation pass over the codepoint SRF. Idempotent.';
