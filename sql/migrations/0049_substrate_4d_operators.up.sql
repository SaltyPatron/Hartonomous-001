-- 0049_substrate_4d_operators.up.sql
--
-- Substrate-extension operators that complete PostGIS's 4D story.
--
-- PostGIS's `ST_3DDistance`, `ST_3DClosestPoint`, etc. give 3D semantics on
-- POINTZ / LINESTRINGZ. PostGIS does NOT provide 4D-spatial semantics — its
-- M coordinate is treated as out-of-band metadata. The substrate's invention
-- requires every coordinate (including M) to be a real spatial axis. These
-- functions provide that semantics on PostGIS-native geometry inputs.
--
-- Naming follows PostGIS's own family: `ST_3DDistance` → `ST_4DDistance`.
-- All functions live in the `substrate` schema (not in `public`) so they
-- compose cleanly alongside PostGIS built-ins without name collision.
--
-- Inputs are PostGIS `geometry` (POINTZM / LINESTRINGZM). Outputs are
-- `float8` for distances or `geometry` for centroids. Implementations are
-- pure SQL — no native extension calls — for clarity and testability.
-- Performance refinement (C implementation in the hartonomous extension)
-- is a future optimization that does not change the SQL contract here.

-- ── 4D Euclidean distance ─────────────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.st_4d_distance(a geometry, b geometry)
RETURNS float8
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT sqrt(
        power(ST_X(a) - ST_X(b), 2) +
        power(ST_Y(a) - ST_Y(b), 2) +
        power(ST_Z(a) - ST_Z(b), 2) +
        power(ST_M(a) - ST_M(b), 2)
    );
$$;

COMMENT ON FUNCTION substrate.st_4d_distance(geometry, geometry) IS
    '4D Euclidean distance between two POINTZM geometries. M is a real spatial axis. Sqrt of sum of squared differences across all four coordinates.';

-- ── S^3 geodesic distance (assumes unit-norm inputs) ──────────────────
CREATE OR REPLACE FUNCTION substrate.st_s3_distance(a geometry, b geometry)
RETURNS float8
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT acos(
        GREATEST(-1.0, LEAST(1.0,
            ST_X(a) * ST_X(b) +
            ST_Y(a) * ST_Y(b) +
            ST_Z(a) * ST_Z(b) +
            ST_M(a) * ST_M(b)
        ))
    );
$$;

COMMENT ON FUNCTION substrate.st_s3_distance(geometry, geometry) IS
    'S^3 geodesic distance between two unit-quaternion POINTZM geometries. Inner product clamped to [-1, 1] then acos. Inputs must be unit-norm; behavior for non-unit inputs is undefined.';

-- ── 4D inner product / norm / normalize ───────────────────────────────
CREATE OR REPLACE FUNCTION substrate.st_4d_dot(a geometry, b geometry)
RETURNS float8
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT ST_X(a)*ST_X(b) + ST_Y(a)*ST_Y(b) + ST_Z(a)*ST_Z(b) + ST_M(a)*ST_M(b);
$$;

CREATE OR REPLACE FUNCTION substrate.st_4d_norm(a geometry)
RETURNS float8
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT sqrt(power(ST_X(a),2) + power(ST_Y(a),2) + power(ST_Z(a),2) + power(ST_M(a),2));
$$;

CREATE OR REPLACE FUNCTION substrate.st_4d_normalize(a geometry)
RETURNS geometry
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    WITH n AS (SELECT substrate.st_4d_norm(a) AS m)
    SELECT CASE
        WHEN n.m = 0 THEN a
        ELSE ST_MakePoint(ST_X(a)/n.m, ST_Y(a)/n.m, ST_Z(a)/n.m, ST_M(a)/n.m)
    END
    FROM n;
$$;

-- ── 4D centroid aggregate ─────────────────────────────────────────────
-- Per-state accumulator: (sumX, sumY, sumZ, sumM, count) → POINTZM centroid.

CREATE OR REPLACE FUNCTION substrate.st_4d_centroid_sfunc(
    state float8[], pt geometry
) RETURNS float8[]
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT ARRAY[
        state[1] + ST_X(pt),
        state[2] + ST_Y(pt),
        state[3] + ST_Z(pt),
        state[4] + ST_M(pt),
        state[5] + 1.0
    ]::float8[];
$$;

CREATE OR REPLACE FUNCTION substrate.st_4d_centroid_finalfunc(state float8[])
RETURNS geometry
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT CASE
        WHEN state[5] = 0 THEN NULL
        ELSE ST_MakePoint(state[1]/state[5], state[2]/state[5], state[3]/state[5], state[4]/state[5])
    END;
$$;

DROP AGGREGATE IF EXISTS substrate.st_4d_centroid(geometry);
CREATE AGGREGATE substrate.st_4d_centroid(geometry) (
    SFUNC     = substrate.st_4d_centroid_sfunc,
    STYPE     = float8[],
    FINALFUNC = substrate.st_4d_centroid_finalfunc,
    INITCOND  = '{0,0,0,0,0}',
    PARALLEL  = SAFE
);

COMMENT ON AGGREGATE substrate.st_4d_centroid(geometry) IS
    '4D centroid of POINTZM geometries. Treats M as a real spatial axis. Returns POINTZM at the average of all four coordinates; NULL on empty input.';

-- ── S^3 centroid aggregate (renormalizes to unit sphere) ──────────────
CREATE OR REPLACE FUNCTION substrate.st_s3_centroid_finalfunc(state float8[])
RETURNS geometry
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    WITH avg AS (
        SELECT state[1]/state[5] AS x, state[2]/state[5] AS y,
               state[3]/state[5] AS z, state[4]/state[5] AS m
        WHERE state[5] > 0
    ),
    n AS (SELECT sqrt(x*x + y*y + z*z + m*m) AS norm, x, y, z, m FROM avg)
    SELECT CASE
        WHEN state[5] = 0 OR (SELECT norm FROM n) = 0 THEN NULL
        ELSE ST_MakePoint(
            (SELECT x FROM n)/(SELECT norm FROM n),
            (SELECT y FROM n)/(SELECT norm FROM n),
            (SELECT z FROM n)/(SELECT norm FROM n),
            (SELECT m FROM n)/(SELECT norm FROM n)
        )
    END;
$$;

DROP AGGREGATE IF EXISTS substrate.st_s3_centroid(geometry);
CREATE AGGREGATE substrate.st_s3_centroid(geometry) (
    SFUNC     = substrate.st_4d_centroid_sfunc,
    STYPE     = float8[],
    FINALFUNC = substrate.st_s3_centroid_finalfunc,
    INITCOND  = '{0,0,0,0,0}',
    PARALLEL  = SAFE
);

COMMENT ON AGGREGATE substrate.st_s3_centroid(geometry) IS
    'S^3-projected centroid: 4D average renormalized to unit length. Use when input POINTZM values are unit quaternions on S^3 and the centroid should remain on the sphere.';

-- ── 4D Frechet distance on LINESTRINGZM ───────────────────────────────
-- Discrete Frechet: O(n*m) DP over coupling matrix using 4D distances.
-- Uses memo-table built from generate_series; safe for moderate-length
-- linestrings (audio/contour scale). For very long linestrings the C
-- implementation in the hartonomous extension is the future optimization.

CREATE OR REPLACE FUNCTION substrate.st_4d_frechet_distance(p geometry, q geometry)
RETURNS float8
LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    n INT := ST_NPoints(p);
    m INT := ST_NPoints(q);
    ca FLOAT8[];
    i INT;
    j INT;
    d FLOAT8;
    pi GEOMETRY;
    qj GEOMETRY;
    p_pts GEOMETRY[];
    q_pts GEOMETRY[];
BEGIN
    IF n = 0 OR m = 0 THEN RETURN NULL; END IF;

    -- Materialize point arrays once to avoid per-cell ST_PointN reparse.
    p_pts := ARRAY(SELECT (ST_DumpPoints(p)).geom ORDER BY (path)[1]);
    q_pts := ARRAY(SELECT (ST_DumpPoints(q)).geom ORDER BY (path)[1]);

    -- Allocate ca[1..n][1..m] flat into a 1D array indexed (i-1)*m + j.
    ca := ARRAY_FILL(-1.0::float8, ARRAY[n * m]);

    -- Bottom-up DP. ca[i,j] = min over predecessors of max(prev, d(p_i, q_j)).
    FOR i IN 1..n LOOP
        pi := p_pts[i];
        FOR j IN 1..m LOOP
            qj := q_pts[j];
            d := substrate.st_4d_distance(pi, qj);
            IF i = 1 AND j = 1 THEN
                ca[1] := d;
            ELSIF i = 1 THEN
                ca[(i-1)*m + j] := GREATEST(ca[(i-1)*m + (j-1)], d);
            ELSIF j = 1 THEN
                ca[(i-1)*m + j] := GREATEST(ca[(i-2)*m + j], d);
            ELSE
                ca[(i-1)*m + j] := GREATEST(
                    LEAST(
                        ca[(i-2)*m + j],
                        ca[(i-2)*m + (j-1)],
                        ca[(i-1)*m + (j-1)]
                    ),
                    d
                );
            END IF;
        END LOOP;
    END LOOP;

    RETURN ca[(n-1)*m + m];
END $$;

COMMENT ON FUNCTION substrate.st_4d_frechet_distance(geometry, geometry) IS
    'Discrete Frechet distance between two LINESTRINGZM in real 4D (M is spatial). O(n*m) DP. For long trajectories, expect to swap in a C implementation.';

-- ── 4D Hausdorff distance ─────────────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.st_4d_hausdorff_distance(p geometry, q geometry)
RETURNS float8
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT GREATEST(
        (SELECT MAX(min_dist) FROM (
            SELECT MIN(substrate.st_4d_distance(pp.geom, qq.geom)) AS min_dist
              FROM ST_DumpPoints(p) pp,
                   ST_DumpPoints(q) qq
             GROUP BY (pp.path)[1]
        ) AS p_to_q),
        (SELECT MAX(min_dist) FROM (
            SELECT MIN(substrate.st_4d_distance(qq.geom, pp.geom)) AS min_dist
              FROM ST_DumpPoints(q) qq,
                   ST_DumpPoints(p) pp
             GROUP BY (qq.path)[1]
        ) AS q_to_p)
    );
$$;

COMMENT ON FUNCTION substrate.st_4d_hausdorff_distance(geometry, geometry) IS
    'Symmetric 4D Hausdorff distance: max over both directions of the closest-point distance. M is a real spatial axis.';

-- ── 4D distance between aggregate centroids (helper for kNN) ──────────
CREATE OR REPLACE FUNCTION substrate.st_4d_distance_to_centroid(
    target geometry, candidates geometry[]
) RETURNS float8
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT substrate.st_4d_distance(target, substrate.st_4d_centroid(c))
      FROM unnest(candidates) AS c;
$$;
