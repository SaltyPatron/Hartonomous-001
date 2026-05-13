-- substrate.cross_model_consensus(p_token_hash bytea)
--
-- Voronoi-tessellation centroid + dispersion + agreement score over a
-- token entity's firefly cloud. Each model that has ingested this token
-- contributed one POINT4D physicality of type embedding_firefly.
--
-- All numerical work runs in compiled C from the hartonomous extension:
--   public.point4d(x,y,z,m)      — native point4d
--   public.centroid_4d(point4d)  — single-pass centroid aggregate (C)
--   public.distance_4d(p,q)      — 4D Euclidean distance (C)
--
-- The SQL function is one flat SELECT — no CTE, no plpgsql loop. Two
-- scans of the cloud are necessary (centroid first, then dispersion
-- against centroid). For typical fireflies-per-token (<= models ingested,
-- usually <100) the cost is dominated by index probe, not the scans.
--
-- Future work: a native firefly_consensus(token_hash bytea) C function
-- in ext/hartonomous_pg/src/ would do centroid + dispersion in one
-- pass over the SPI cursor — single-pass, all C, no SQL composition.
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
          SELECT public.centroid_4d(p.geom::point4d)                     AS centroid,
                 count(*)::int                                            AS n
            FROM substrate.physicality p
            JOIN substrate.physicality_type pt
              ON pt.id   = p.physicality_type_id
             AND pt.code = 'embedding_firefly'
           WHERE p.entity_hash = p_token_hash
      ) c
      CROSS JOIN LATERAL (
          SELECT max(public.distance_4d(p.geom::point4d, c.centroid))       AS max_dist
            FROM substrate.physicality p
            JOIN substrate.physicality_type pt
              ON pt.id   = p.physicality_type_id
             AND pt.code = 'embedding_firefly'
           WHERE p.entity_hash = p_token_hash
      ) d;
$$;

COMMENT ON FUNCTION substrate.cross_model_consensus(bytea) IS
    'Centroid + dispersion + agreement over a token''s firefly cloud. All math via native hartonomous primitives (point4d, centroid_4d aggregate, distance_4d). One SQL function, no CTE, no plpgsql.';
