-- Substrate-native bulk physicality write. Set-based INSERT via unnest +
-- ON CONFLICT DO NOTHING. Replaces pg_temp.physicality_inflight + COPY +
-- INSERT-SELECT pattern.
--
-- Parameters are 5 parallel arrays: physicality_type_id, entity_hash,
-- content_hash, partition_bucket (= entity_hash byte 0 & 7), geometry_payload
-- (EWKB BYTEA). geom is constructed server-side via ST_GeomFromEWKB.
CREATE OR REPLACE FUNCTION substrate.write_physicalities(
    p_type_ids         INT[],
    p_entity_hashes    BYTEA[],
    p_content_hashes   BYTEA[],
    p_geometry_payloads BYTEA[]
)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    n_in      INT;
    n_written INT;
BEGIN
    n_in := COALESCE(cardinality(p_entity_hashes), 0);
    IF n_in = 0 THEN RETURN 0; END IF;
    IF cardinality(p_type_ids) <> n_in
       OR cardinality(p_content_hashes) <> n_in
       OR cardinality(p_geometry_payloads) <> n_in THEN
        RAISE EXCEPTION 'write_physicalities: array length mismatch (% / % / % / %)',
            n_in, cardinality(p_type_ids), cardinality(p_content_hashes),
            cardinality(p_geometry_payloads);
    END IF;

    INSERT INTO substrate.physicality
        (physicality_type_id, entity_hash, content_hash, geom, partition_bucket)
    SELECT DISTINCT ON (t.type_id, t.entity_hash, t.content_hash)
           t.type_id,
           t.entity_hash::substrate.hash_value,
           t.content_hash::substrate.hash_value,
           ST_GeomFromEWKB(t.geometry_payload),
           (get_byte(t.entity_hash, 0) & 7)::SMALLINT AS partition_bucket
      FROM unnest(p_type_ids, p_entity_hashes, p_content_hashes, p_geometry_payloads)
           AS t(type_id, entity_hash, content_hash, geometry_payload)
     ORDER BY t.type_id, t.entity_hash, t.content_hash
    ON CONFLICT (physicality_type_id, entity_hash, content_hash, partition_bucket) DO NOTHING;

    GET DIAGNOSTICS n_written = ROW_COUNT;
    RETURN n_written;
END $$;

COMMENT ON FUNCTION substrate.write_physicalities(INT[], BYTEA[], BYTEA[], BYTEA[]) IS
    'Substrate-native bulk physicality write. INSERT via unnest + ON CONFLICT DO NOTHING on (physicality_type, entity_hash, content_hash, partition_bucket). geom constructed via ST_GeomFromEWKB from caller-supplied EWKB payload. partition_bucket computed server-side from entity_hash byte 0.';
