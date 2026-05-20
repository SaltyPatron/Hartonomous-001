-- Substrate-native bulk entity_classification write. Set-based INSERT via
-- unnest + ON CONFLICT DO NOTHING. Replaces pg_temp.entity_classification_inflight
-- + COPY + INSERT-SELECT.
--
-- Parameters are 3 parallel arrays: entity hash, entity_type_id, provenance_id.
-- Caller resolves type/provenance codes to ids before calling.
CREATE OR REPLACE FUNCTION substrate.write_entity_classifications(
    p_entity_hashes  BYTEA[],
    p_type_ids       INT[],
    p_provenance_ids INT[]
)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    n_in        INT;
    n_written   INT;
BEGIN
    n_in := COALESCE(cardinality(p_entity_hashes), 0);
    IF n_in = 0 THEN RETURN 0; END IF;
    IF cardinality(p_type_ids) <> n_in OR cardinality(p_provenance_ids) <> n_in THEN
        RAISE EXCEPTION 'write_entity_classifications: array length mismatch (% / % / %)',
            n_in, cardinality(p_type_ids), cardinality(p_provenance_ids);
    END IF;

    -- Sort the deduplicated rows before INSERT to enforce consistent
    -- lock acquisition order across concurrent worker calls. Without
    -- ORDER BY, two workers with overlapping (entity_hash, type, prov)
    -- triples can each grab a row-lock the other also wants and deadlock
    -- on ON CONFLICT resolution (PG 40P01).
    INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
    SELECT entity_hash, entity_type_id, provenance_id
      FROM (SELECT DISTINCT
                   t.entity_hash::substrate.hash_value AS entity_hash,
                   t.type_id AS entity_type_id,
                   t.provenance_id
              FROM unnest(p_entity_hashes, p_type_ids, p_provenance_ids)
                   AS t(entity_hash, type_id, provenance_id)) ordered
     ORDER BY entity_hash, entity_type_id, provenance_id
    ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING;

    GET DIAGNOSTICS n_written = ROW_COUNT;
    RETURN n_written;
END $$;

COMMENT ON FUNCTION substrate.write_entity_classifications(BYTEA[], INT[], INT[]) IS
    'Substrate-native bulk entity_classification write. INSERT via unnest + ON CONFLICT DO NOTHING on (entity_hash, entity_type_id, provenance_id). Caller resolves codes to ids first; pipeline already caches the lookups.';
