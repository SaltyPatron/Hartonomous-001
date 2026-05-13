-- substrate.apply_firefly_rotation(p_model_source_id, R 3x3)
--
-- Rotate every embedding_firefly POINTZM physicality of a given
-- model_source by a 3×3 orthogonal matrix R, leaving the M coordinate
-- (L2 magnitude) untouched. Run after EmbeddingFireflyPass for non-anchor
-- models. R must be orthogonal (det = +1); the caller is responsible —
-- Procrustes (Kabsch) returns such an R.
--
-- PostGIS-native geom: builds the rotated point via ST_MakePoint(x, y, z, m)
-- — returns geometry(POINTZM). The original (X, Y, Z) extracted via
-- ST_X / ST_Y / ST_Z; M passed through unchanged.
CREATE OR REPLACE FUNCTION substrate.apply_firefly_rotation(
    p_model_source_id INT,
    p_r00 FLOAT8, p_r01 FLOAT8, p_r02 FLOAT8,
    p_r10 FLOAT8, p_r11 FLOAT8, p_r12 FLOAT8,
    p_r20 FLOAT8, p_r21 FLOAT8, p_r22 FLOAT8
) RETURNS BIGINT
LANGUAGE SQL
VOLATILE
AS $$
    WITH updated AS (
        UPDATE substrate.physicality p
           SET geom = ST_MakePoint(
                  p_r00 * ST_X(p.geom)
                      + p_r01 * ST_Y(p.geom)
                      + p_r02 * ST_Z(p.geom),
                  p_r10 * ST_X(p.geom)
                      + p_r11 * ST_Y(p.geom)
                      + p_r12 * ST_Z(p.geom),
                  p_r20 * ST_X(p.geom)
                      + p_r21 * ST_Y(p.geom)
                      + p_r22 * ST_Z(p.geom),
                  ST_M(p.geom)
              )
          FROM substrate.entity_model_source ems,
              substrate.physicality_type pt
         WHERE p.entity_hash         = ems.entity_hash
           AND ems.model_source_id   = p_model_source_id
           AND p.physicality_type_id = pt.id
           AND pt.code               = 'embedding_firefly'
        RETURNING 1
    )
    SELECT count(*)::BIGINT FROM updated;
$$;

COMMENT ON FUNCTION substrate.apply_firefly_rotation(INT, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8) IS
    'Rotate every embedding_firefly POINTZM of one model_source by a 3×3 orthogonal R. M (L2 magnitude) preserved. Caller (Procrustes/Kabsch) ensures det(R)=+1. Returns count of rotated rows.';
