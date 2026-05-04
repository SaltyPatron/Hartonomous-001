-- substrate.populate_scripts_from_ext()
--
-- Drives substrate.script from the embedded UCD catalog. The extension's
-- ucd_scripts() SETOF returns just (id, code) — substrate.script's only
-- distinguishing column is `code`, so we map directly.
--
-- Idempotent — ON CONFLICT (code) DO NOTHING.

CREATE OR REPLACE FUNCTION substrate.populate_scripts_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.script (code)
    SELECT v.code
    FROM substrate.ucd_scripts() AS v
    WHERE v.code IS NOT NULL AND length(v.code) > 0
    ON CONFLICT (code) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_scripts_from_ext() IS
    'Bulk-loads substrate.script from the embedded UCD catalog. Idempotent. Returns the number of rows inserted on this call.';
