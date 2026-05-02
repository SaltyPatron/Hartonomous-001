-- Reverse 0021: restore the un-guarded drain_staging_entity_model_source_chunk.
--
-- WARNING: down migration restores the body that races
-- substrate.staging_entity_model_source against substrate.staging_entity,
-- which trips the entity_model_source_entity_type_id_entity_hash_fkey
-- composite FK whenever the producer outruns the entity drain. Only run
-- in tear-down contexts.

CREATE OR REPLACE FUNCTION substrate.drain_staging_entity_model_source_chunk(p_chunk_size INT DEFAULT 4096)
RETURNS BIGINT
LANGUAGE plpgsql
SET jit = off
SET max_parallel_workers_per_gather = 0
SET max_parallel_maintenance_workers = 0
AS $$
DECLARE
    v_drained BIGINT;
BEGIN
    WITH claimed AS (
        SELECT ctid, entity_type_id, entity_hash, model_source_id
          FROM substrate.staging_entity_model_source
         LIMIT p_chunk_size
           FOR UPDATE SKIP LOCKED
    ),
    inserted AS (
        INSERT INTO substrate.entity_model_source
            (entity_type_id, entity_hash, model_source_id)
        SELECT DISTINCT entity_type_id, entity_hash, model_source_id FROM claimed
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
    DELETE FROM substrate.staging_entity_model_source
     WHERE ctid IN (SELECT ctid FROM claimed);

    GET DIAGNOSTICS v_drained = ROW_COUNT;
    RETURN v_drained;
END $$;
