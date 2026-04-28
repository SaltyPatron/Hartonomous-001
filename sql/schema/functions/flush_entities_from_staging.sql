-- Drains staging_entity into substrate.entity, one partition at a time.
-- The C# pipeline COPYs (entity_type_id, hash) tuples into a TEMP table named
-- staging_entity (created in the same transaction) and then calls this
-- function. We loop over DISTINCT entity_type_ids and issue a per-type
-- INSERT, so PG's tuple-router sees only one partition per statement —
-- bypassing the multi-partition routing path that destabilises the backend
-- under bulk load with the hartonomous extension loaded (task #86).
--
-- Substrate-faithful per AP-2: C# constructs no INSERT SQL; it COPYs binary
-- and calls this function by name. ON CONFLICT (entity_type_id, hash)
-- preserves dedup semantics on the parent's primary key.
CREATE OR REPLACE FUNCTION substrate.flush_entities_from_staging()
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    t INT;
BEGIN
    FOR t IN SELECT DISTINCT entity_type_id FROM staging_entity LOOP
        INSERT INTO substrate.entity (entity_type_id, hash)
        SELECT DISTINCT entity_type_id, hash FROM staging_entity
        WHERE entity_type_id = t
        ON CONFLICT (entity_type_id, hash) DO NOTHING;
    END LOOP;
END $$;
