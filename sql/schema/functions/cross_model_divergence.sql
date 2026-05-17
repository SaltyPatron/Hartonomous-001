-- substrate.cross_model_divergence(p_token_hash bytea, p_model_a_arch_hash bytea, p_model_b_arch_hash bytea)
--
-- Pairwise 4D Euclidean distance between two models' fireflies for the
-- same token entity. Returns NULL when either model has no firefly for
-- the token. Drives D-cross-model-divergence-nonzero gate.
--
-- PostGIS-native: extracts (X, Y, Z, M) via ST_X / ST_Y / ST_Z / ST_M from
-- POINTZM geometry directly.
DROP FUNCTION IF EXISTS substrate.cross_model_divergence(bytea, bytea, bytea);
CREATE OR REPLACE FUNCTION substrate.cross_model_divergence(
    p_token_hash         bytea,
    p_model_a_arch_hash  bytea,
    p_model_b_arch_hash  bytea
)
RETURNS DOUBLE PRECISION
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH a AS (
        SELECT ST_X(p.geom) AS x,
               ST_Y(p.geom) AS y,
               ST_Z(p.geom) AS z,
               ST_M(p.geom) AS m
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id AND pt.code = 'firefly'
          JOIN substrate.entity_model_source ems_t ON ems_t.entity_hash = p.entity_hash
          JOIN substrate.entity_model_source ems_a
            ON ems_a.model_source_id = ems_t.model_source_id
           AND ems_a.entity_hash = p_model_a_arch_hash
         WHERE p.entity_hash = p_token_hash
    ),
    b AS (
        SELECT ST_X(p.geom) AS x,
               ST_Y(p.geom) AS y,
               ST_Z(p.geom) AS z,
               ST_M(p.geom) AS m
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id AND pt.code = 'firefly'
          JOIN substrate.entity_model_source ems_t ON ems_t.entity_hash = p.entity_hash
          JOIN substrate.entity_model_source ems_b
            ON ems_b.model_source_id = ems_t.model_source_id
           AND ems_b.entity_hash = p_model_b_arch_hash
         WHERE p.entity_hash = p_token_hash
    )
    SELECT sqrt((a.x - b.x) ^ 2 + (a.y - b.y) ^ 2 + (a.z - b.z) ^ 2 + (a.m - b.m) ^ 2)
      FROM a, b;
$$;

COMMENT ON FUNCTION substrate.cross_model_divergence(bytea, bytea, bytea) IS
    'Pairwise 4D distance between model A''s and model B''s fireflies for a shared token. Reads PostGIS POINTZM coords directly via ST_X / ST_Y / ST_Z / ST_M.';
