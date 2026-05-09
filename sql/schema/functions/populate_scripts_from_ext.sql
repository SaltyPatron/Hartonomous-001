-- substrate.populate_scripts_from_ext()
--
-- Drives substrate.script from the embedded UCD catalog. The extension's
-- ucd_scripts() SETOF returns (id, code). Reference table IDs are pinned to
-- extension_id + 1 so high-volume codepoint_property loading can project FK
-- IDs directly without per-row reference joins.
--
-- Idempotent on the deterministic ID. A conflicting code at another ID is a
-- data-corruption signal, not something to silently merge.

CREATE OR REPLACE FUNCTION substrate.populate_scripts_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.script (id, code)
    SELECT v.id + 1, v.code
    FROM substrate.ucd_scripts() AS v
    WHERE v.code IS NOT NULL AND length(v.code) > 0
    ON CONFLICT (id) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;

    PERFORM setval(pg_get_serial_sequence('substrate.script', 'id'),
                   (SELECT max(id) FROM substrate.script), true);

    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_scripts_from_ext() IS
    'Bulk-loads substrate.script from the embedded UCD catalog with id = extension_id + 1. Idempotent. Returns the number of rows inserted on this call.';
