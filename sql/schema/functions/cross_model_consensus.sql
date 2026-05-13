-- substrate.cross_model_consensus(p_token_hash bytea)
--
-- Voronoi-tessellation centroid + dispersion + agreement score over a
-- token entity's firefly cloud. Each model that has ingested this token
-- contributed one POINTZM physicality of type embedding_firefly.
--
-- PostGIS-native: physicality.geom is geometry(POINTZM); cast to
-- public.point4d via the geometry->point4d bridge for libhartonomous
-- kernel calls (centroid_4d aggregate, distance_4d).
DROP FUNCTION IF EXISTS substrate.cross_model_consensus(bytea);
CREATE OR REPLACE FUNCTION substrate.cross_model_consensus(p_token_hash bytea)
RETURNS TABLE (
    centroid        public.point4d,
    n_contributing  int,
    dispersion_max  double precision,
    agreement_score double precision
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT
        c.centroid,
        c.n,
        d.max_dist,
        CASE WHEN c.n = 0 THEN NULL
             ELSE 1.0 / (1.0 + COALESCE(d.max_dist, 0.0))
        END
      FROM (
          SELECT public.centroid_4d(p.geom::public.point4d) AS centroid,
                 count(*)::int                              AS n
            FROM substrate.physicality p
            JOIN substrate.physicality_type pt
              ON pt.id   = p.physicality_type_id
             AND pt.code = 'embedding_firefly'
           WHERE p.entity_hash = p_token_hash
      ) c
      CROSS JOIN LATERAL (
          SELECT max(public.distance_4d(p.geom::public.point4d, c.centroid)) AS max_dist
            FROM substrate.physicality p
            JOIN substrate.physicality_type pt
              ON pt.id   = p.physicality_type_id
             AND pt.code = 'embedding_firefly'
           WHERE p.entity_hash = p_token_hash
      ) d;
$$;

COMMENT ON FUNCTION substrate.cross_model_consensus(bytea) IS
    'Centroid + dispersion + agreement over a token''s firefly cloud. PostGIS POINTZM cast to public.point4d via the geometry->point4d bridge; aggregates via the libhartonomous centroid_4d + distance_4d native kernels.';
