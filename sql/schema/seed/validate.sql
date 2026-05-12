-- Seed inventory check. Set-based: collects every count that diverges
-- from the canonical inventory in one pass and raises with the full list,
-- so a fresh-DB apply doesn't fail on the first count and hide the rest.
DO $$
DECLARE
    failures TEXT[] := ARRAY[]::TEXT[];
    rec      RECORD;
    actual   BIGINT;
BEGIN
    FOR rec IN
        SELECT * FROM (VALUES
            ('substrate.entity_type',           23),
            ('substrate.physicality_type',      14),
            ('substrate.edge_role',              7),
            ('substrate.significance_context',  10),
            ('substrate.provenance',            10),
            ('substrate.bidi_class',            23),
            ('substrate.east_asian_width',       6),
            ('substrate.lexname',               45),
            ('substrate.pos',                   17),
            ('substrate.edge_type',            120),
            ('substrate.attestation_type',      27)
        ) AS t(table_name, expected)
    LOOP
        EXECUTE format('SELECT count(*) FROM %s', rec.table_name) INTO actual;
        IF actual <> rec.expected THEN
            failures := array_append(failures,
                format('%s = %s (expected %s)', rec.table_name, actual, rec.expected));
        END IF;
    END LOOP;

    IF array_length(failures, 1) IS NOT NULL THEN
        RAISE EXCEPTION 'seed inventory mismatch: %', array_to_string(failures, '; ');
    END IF;
END $$;
