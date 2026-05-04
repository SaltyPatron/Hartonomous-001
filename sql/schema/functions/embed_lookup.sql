-- substrate.embed_lookup(seed_hash, entity_type_code, k, distance_kind)
--
-- Top-k entities by 4D distance from the seed's stored physicality. The seed
-- supplies its own geometry; the candidate set is filtered by entity_type
-- (which lives on substrate.entity_classification, since substrate.entity is
-- hash-only). All inner work — neighbor enumeration, distance evaluation,
-- top-k heap — happens inside the pg_similarity_topk C SRF; this plpgsql
-- function only resolves the seed centroid and the entity-type filter, then
-- hands the candidate query to the C kernel.
--
-- Distance kinds:
--   '4d'      → substrate.dist_4d (POINTZM short-circuits to native
--               distance_4d; multi-vertex geometries fall through to native
--               frechet_4d via ST_DumpPoints).
--   'frechet' → substrate.frechet_4d_geom (always Fréchet over depth-first
--               vertex sequence, even for two POINTs — costs more, but
--               useful when comparing trajectory shapes).
--   's3'      → reserved for unit-quaternion S3 distance; not yet wired
--               (substrate.dist_s3(geometry, geometry) wrapper is a TODO).
--               pg_similarity_topk will ereport on this kind today.
DROP FUNCTION IF EXISTS substrate.embed_lookup(BYTEA, TEXT, INT, TEXT, DOUBLE PRECISION);
CREATE OR REPLACE FUNCTION substrate.embed_lookup(
    p_seed_hash         BYTEA,
    p_entity_type_code  TEXT,
    p_k                 INT              DEFAULT 10,
    p_distance_kind     TEXT             DEFAULT '4d',
    p_distance_threshold DOUBLE PRECISION DEFAULT NULL
) RETURNS TABLE (
    entity_type_id INT,
    entity_hash    BYTEA,
    distance       DOUBLE PRECISION,
    elapsed_ms     INT
)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_started          TIMESTAMP := clock_timestamp();
    v_entity_type_id   INT;
    v_seed_geom        GEOMETRY;
    v_candidate_query  TEXT;
BEGIN
    SELECT id INTO v_entity_type_id
    FROM substrate.entity_type
    WHERE code = p_entity_type_code;

    IF v_entity_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown entity_type code: %', p_entity_type_code
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    -- Resolve the seed centroid. Take the first physicality available for
    -- this entity (most entities have exactly one; multi-physicality entities
    -- like firefly atoms get the lowest physicality_type_id deterministically).
    SELECT geom INTO v_seed_geom
    FROM substrate.physicality
    WHERE entity_hash = p_seed_hash
    ORDER BY physicality_type_id
    LIMIT 1;

    IF v_seed_geom IS NULL THEN
        RAISE EXCEPTION 'seed entity has no physicality: hash=%',
            encode(p_seed_hash, 'hex')
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    -- Candidate query: every entity classified as the requested type that
    -- has a physicality. The (entity_type_id, entity_hash) index on
    -- substrate.entity_classification gives O(log N) bounded scan; the JOIN
    -- to physicality is selective via the same hash. We exclude the seed
    -- itself from candidates.
    v_candidate_query := format(
        'SELECT %s::int AS entity_type_id, p.entity_hash, p.geom '
        || 'FROM substrate.entity_classification c '
        || 'JOIN substrate.physicality p ON p.entity_hash = c.entity_hash '
        || 'WHERE c.entity_type_id = %s '
        || '  AND c.entity_hash <> %L::bytea',
        v_entity_type_id,
        v_entity_type_id,
        p_seed_hash);

    RETURN QUERY
    SELECT
        s.entity_type_id,
        s.entity_hash,
        s.distance,
        EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT AS elapsed_ms
    FROM substrate.similarity_topk(
        v_seed_geom,
        p_k,
        p_distance_kind,
        v_candidate_query,
        p_distance_threshold) s;
END $$;

COMMENT ON FUNCTION substrate.embed_lookup(BYTEA, TEXT, INT, TEXT, DOUBLE PRECISION) IS
    'Top-k entities by 4D distance from the seed entity''s stored physicality, filtered to a target entity_type via substrate.entity_classification. Uses the pg_similarity_topk C SRF for the inner scan and heap. Distance kinds: 4d (default; POINTZM fast path) | frechet (always vertex-stream Fréchet) | s3 (reserved, not yet wired).';
