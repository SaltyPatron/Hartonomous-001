-- Drains staging_physicality into substrate.physicality, one partition
-- (physicality_type_id) at a time. ST_GeomFromWKB decodes the binary WKB
-- payload server-side; the per-partition CHECK constraints (LINESTRINGZM
-- vs POINTZM dimensionality) get evaluated on the correct partition.
CREATE OR REPLACE FUNCTION substrate.flush_physicality_from_staging()
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    t INT;
BEGIN
    FOR t IN SELECT DISTINCT physicality_type_id FROM staging_physicality LOOP
        INSERT INTO substrate.physicality
            (physicality_type_id, entity_type_id, entity_hash, content_hash, geom)
        SELECT
            physicality_type_id, entity_type_id, entity_hash, content_hash,
            ST_GeomFromWKB(wkb)
        FROM staging_physicality
        WHERE physicality_type_id = t
        ON CONFLICT (physicality_type_id, entity_type_id, entity_hash, content_hash)
        DO NOTHING;
    END LOOP;
END $$;
