-- Bridge from PostGIS-native LINESTRINGZM geometry to the internal native
-- compute ABI type public.linestring4d. Used internally by substrate.st_4d_*
-- operator dispatch — every substrate-level function takes geometry (the
-- storage type) and converts at the kernel boundary via this cast.
-- public.linestring4d is NOT a substrate-level user-visible type (per
-- .claude/rules/25-physicality-4d.md); it's the internal flat-array I/O ABI
-- for the native kernels.
CREATE OR REPLACE FUNCTION public.linestring4d_from_geometry(g geometry)
RETURNS public.linestring4d
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT public.array_to_linestring4d(
        ARRAY(
            SELECT coord
              FROM generate_series(1, ST_NumPoints(g)) AS idx(i)
              CROSS JOIN LATERAL (
                  SELECT ST_PointN(g, idx.i) AS p
              ) pt
              CROSS JOIN LATERAL (
                  SELECT unnest(ARRAY[ST_X(pt.p), ST_Y(pt.p), ST_Z(pt.p), ST_M(pt.p)])
              ) AS axes(coord)
        )
    )
$$;

COMMENT ON FUNCTION public.linestring4d_from_geometry(geometry) IS
    'Walk a LINESTRINGZM''s vertices via ST_PointN, build a flat (x,y,z,m,x,y,z,m,...) array, and construct the internal native linestring4d. Used by substrate.st_4d_* operator dispatch to bridge PostGIS storage to libhartonomous kernel I/O.';

CREATE CAST (geometry AS public.linestring4d)
    WITH FUNCTION public.linestring4d_from_geometry(geometry)
    AS ASSIGNMENT;
