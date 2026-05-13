-- Bridge from PostGIS-native POINTZM geometry to the internal native compute
-- ABI type public.point4d (zero-marshalling flat (x,y,z,m) for libhartonomous
-- C kernels). Used internally by substrate.st_4d_* operator dispatch — every
-- substrate-level function takes geometry (the storage type) and converts at
-- the kernel boundary via this cast. public.point4d is NOT a substrate-level
-- user-visible type (per .claude/rules/25-physicality-4d.md); it's the
-- internal flat-array I/O ABI for the native kernels.
CREATE OR REPLACE FUNCTION public.point4d_from_geometry(g geometry)
RETURNS public.point4d
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT public.point4d(ST_X(g), ST_Y(g), ST_Z(g), ST_M(g))
$$;

COMMENT ON FUNCTION public.point4d_from_geometry(geometry) IS
    'Extract (X, Y, Z, M) from a POINTZM and construct the internal native point4d. Used by substrate.st_4d_* operator dispatch to bridge PostGIS storage to libhartonomous kernel I/O.';

CREATE CAST (geometry AS public.point4d)
    WITH FUNCTION public.point4d_from_geometry(geometry)
    AS ASSIGNMENT;
