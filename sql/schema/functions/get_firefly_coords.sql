-- substrate.get_firefly_coords(p_bpe_token_entity_hashes BYTEA[], p_model_source_id INT)
--
-- Return per-entity firefly POINTZM (X, Y, Z) for a vocab intersection
-- set, scoped to one model_source. Used by EmbeddingAlignmentPass to pull
-- the (anchor, this-model) coordinate pairs into managed memory for
-- Procrustes/Kabsch fitting.
--
-- PostGIS-native: physicality.geom is geometry(POINTZM); ST_X / ST_Y / ST_Z
-- extract coordinates directly without going through point4d_to_array.
-- M (L2 magnitude) intentionally omitted — Kabsch rotation operates on
-- the 3D direction and M is preserved separately.
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
           ST_X(p.geom) AS x,
           ST_Y(p.geom) AS y,
           ST_Z(p.geom) AS z
      FROM substrate.physicality p
      JOIN substrate.entity_model_source ems
        ON ems.entity_hash = p.entity_hash
      JOIN substrate.physicality_type pt
        ON pt.id = p.physicality_type_id
     WHERE p.entity_hash = ANY(p_bpe_token_entity_hashes)
       AND ems.model_source_id = p_model_source_id
       AND pt.code = 'firefly'
     ORDER BY p.entity_hash ASC;
$$;

COMMENT ON FUNCTION substrate.get_firefly_coords(BYTEA[], INT) IS
    'Per-entity firefly XYZ for a vocab intersection set, scoped to one model_source. Ordered by entity_hash ASC so cross-model calls return aligned arrays. Used by EmbeddingAlignmentPass for Procrustes input.';
