-- substrate.get_firefly_coords(p_bpe_token_entity_hashes BYTEA[], p_model_source_id INT)
--
-- Return per-entity firefly POINT4D coordinates for a vocab intersection
-- set, scoped to one model_source. Used by EmbeddingAlignmentPass to pull
-- the (anchor, this-model) coordinate pairs into managed memory for
-- Procrustes/Kabsch fitting.
--
-- Hash-as-PK: input is an array of entity_hash BYTEAs, not surrogate ids.
-- Output rows are ordered by entity_hash ASC so two calls (anchor model,
-- this model) for the same hash set yield aligned column orderings.

CREATE OR REPLACE FUNCTION substrate.get_firefly_coords(
    p_bpe_token_entity_hashes BYTEA[],
    p_model_source_id         INT
) RETURNS TABLE (
    entity_hash BYTEA,
    x           FLOAT8,
    y           FLOAT8,
    z           FLOAT8
)
LANGUAGE SQL
STABLE
AS $$
    SELECT p.entity_hash,
           coords.v[1] AS x,
           coords.v[2] AS y,
           coords.v[3] AS z
      FROM substrate.physicality p
      JOIN substrate.entity_model_source ems
        ON ems.entity_hash = p.entity_hash
      JOIN substrate.physicality_type pt
        ON pt.id = p.physicality_type_id
      CROSS JOIN LATERAL (SELECT point4d_to_array(p.geom::point4d) AS v) AS coords
     WHERE p.entity_hash = ANY(p_bpe_token_entity_hashes)
       AND ems.model_source_id = p_model_source_id
       AND pt.code = 'embedding_firefly'
     ORDER BY p.entity_hash ASC;
$$;

COMMENT ON FUNCTION substrate.get_firefly_coords(BYTEA[], INT) IS
    'Per-entity firefly XYZ coords for a vocab intersection set, scoped to one model_source. Ordered by entity_hash ASC so cross-model calls return aligned arrays. Used by EmbeddingAlignmentPass for Procrustes input.';
