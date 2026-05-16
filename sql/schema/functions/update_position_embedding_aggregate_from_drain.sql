-- Incremental drain-time update of the position_embedding_aggregate table.
-- Called by StreamingIngestionPipeline's drain-completion post-pass with
-- the array of parent-entity hashes that landed in this drain (filtered to
-- content-tier types: text_composition / paragraph / document).
--
-- UPSERTs counts; new trajectories add to existing per-(ordinal, child_hash)
-- buckets; same content seen N times across drains adds N occurrences.
-- Per AP-37: idempotent at the row level (since content is content-addressed,
-- same trajectory hash re-ingested is identical — but adding to count is
-- correct semantically: each ingestion event IS a frequency observation).
CREATE OR REPLACE FUNCTION substrate.update_position_embedding_aggregate_from_drain(
    p_parent_hashes BYTEA[]
)
RETURNS BIGINT
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows_upserted BIGINT := 0;
BEGIN
    IF p_parent_hashes IS NULL OR array_length(p_parent_hashes, 1) IS NULL THEN
        RETURN 0;
    END IF;

    -- Restrict to content-tier parents (text_composition / paragraph / document).
    -- Word_form / lemma compositions ARE entity-tier (the brick's internal
    -- structure) and should not contribute to position embedding statistics;
    -- only content-tier trajectories carry meaningful positional ordering.
    WITH eligible_parents AS (
        SELECT DISTINCT ec.entity_hash
          FROM unnest(p_parent_hashes) AS h(hash)
          JOIN substrate.entity_classification ec
            ON ec.entity_hash = h.hash
         WHERE ec.entity_type_id IN (
             SELECT id FROM substrate.entity_type
              WHERE code IN ('text_composition', 'paragraph', 'document')
         )
    ),
    new_observations AS (
        SELECT
            (ch.ordinal - 1)::INT AS ordinal,
            ch.child_hash,
            count(*)::BIGINT AS occurrences
          FROM eligible_parents ep,
               LATERAL substrate.get_composition_children(ep.entity_hash) ch
         WHERE ch.ordinal >= 1
           AND ch.ordinal <= 65535
         GROUP BY ch.ordinal, ch.child_hash
    )
    INSERT INTO substrate.position_embedding_aggregate (ordinal, child_hash, occurrences)
    SELECT ordinal, child_hash, occurrences
      FROM new_observations
    ON CONFLICT (ordinal, child_hash) DO UPDATE
       SET occurrences = substrate.position_embedding_aggregate.occurrences + EXCLUDED.occurrences;

    GET DIAGNOSTICS v_rows_upserted = ROW_COUNT;
    RETURN v_rows_upserted;
END;
$$;

COMMENT ON FUNCTION substrate.update_position_embedding_aggregate_from_drain(BYTEA[]) IS
    'Incremental drain-time update of substrate.position_embedding_aggregate. Called per drain by StreamingIngestionPipeline with new content-trajectory parent hashes. UPSERTs per-(ordinal, child_hash) counts. AP-37 drain-as-state-change pattern.';
