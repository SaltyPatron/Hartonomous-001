-- substrate.flush_sequence_from_staging()
--
-- Drains staging_sequence into substrate.sequence, one partition at a time
-- (per the same per-partition routing pattern used by flush_entities,
-- flush_edges, flush_edge_members — sidesteps PG18.3 multi-partition
-- tuple-router crashes under bulk INSERT).
--
-- ON CONFLICT (parent_entity_type_id, parent_entity_hash, ordinal) DO
-- NOTHING — re-running an idempotent decomposition produces no duplicate
-- sequence rows because (parent_hash, ordinal) is fully content-addressed.
CREATE OR REPLACE FUNCTION substrate.flush_sequence_from_staging()
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    t INT;
BEGIN
    FOR t IN SELECT DISTINCT parent_entity_type_id FROM staging_sequence LOOP
        INSERT INTO substrate.sequence (
            parent_entity_type_id,
            parent_entity_hash,
            ordinal,
            child_entity_type_id,
            child_entity_hash,
            rle_count
        )
        SELECT DISTINCT
            parent_entity_type_id,
            parent_entity_hash,
            ordinal,
            child_entity_type_id,
            child_entity_hash,
            rle_count
          FROM staging_sequence
         WHERE parent_entity_type_id = t
        ON CONFLICT (parent_entity_type_id, parent_entity_hash, ordinal)
        DO NOTHING;
    END LOOP;
END $$;

COMMENT ON FUNCTION substrate.flush_sequence_from_staging() IS
    'Per-partition flush of staging_sequence → substrate.sequence. Idempotent on (parent, ordinal).';
