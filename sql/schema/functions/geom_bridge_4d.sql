-- ============================================================================
-- Substrate 4D operator surface — subtype-aware bridge between PostGIS
-- GeometryZM storage and libhartonomous native compute.
-- ============================================================================
-- Storage is universal: substrate.physicality.geom is geometry(GeometryZM),
-- accepting the full GeometryZM subtype family (POINTZM, LINESTRINGZM,
-- MULTILINESTRINGZM, POLYGONZM, MULTIPOLYGONZM, MULTIPOINTZM,
-- GEOMETRYCOLLECTIONZM). Per-partition CHECK constraints declare which
-- subtype(s) and which axis semantics each physicality_type uses.
--
-- Compute lives in libhartonomous via the C extension. Two native primitives
-- carry all the load:
--   public.distance_4d(point4d, point4d) → 4D Euclidean
--   public.frechet_4d(linestring4d, linestring4d) → discrete Fréchet
--   public.hausdorff_4d(linestring4d, linestring4d) → symmetric Hausdorff
-- (point4d / linestring4d are internal native compute primitives, NOT
-- substrate-level types. They exist so the C kernels can take a flat
-- (x,y,z,m) sequence with zero PostGIS marshalling overhead.)
--
-- The substrate-side operators below dispatch on GeometryType and route to
-- the appropriate native primitive while preserving subtype structure:
--   * POINT-vs-POINT     → distance_4d
--   * LINESTRING-vs-LINESTRING → frechet_4d / hausdorff_4d on the linestring
--   * MULTILINESTRING    → minimum across pairwise component frechet
--   * POLYGON            → exterior ring as the structural trajectory
--   * MULTIPOLYGON       → minimum across pairwise component frechet
--   * GEOMETRYCOLLECTION → minimum across all component pairs
--   * MULTIPOINT         → Hausdorff (Fréchet undefined on unordered sets)
--   * Cross-shape pairs  → representative-point or vertex-stream fallback
--
-- This is explicitly NOT "ST_DumpPoints flatten everything" — that approach
-- loses subtype structural distinction (ring concatenation in polygons,
-- branch concatenation in multilinestrings, etc.) and produces wrong answers
-- for non-trivial subtype combinations.

-- ────────────────────────────────────────────────────────────────────────────
-- Helper: walk one geometry's vertex stream into a native linestring4d.
-- Used by dispatch arms that genuinely DO want the flat sequence (LINESTRING
-- treated as a single trajectory, MULTIPOINT treated as an unordered set for
-- Hausdorff). Callers that need subtype structure preserved must dispatch
-- on GeometryType BEFORE building the linestring4d.
-- ────────────────────────────────────────────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.geom_to_linestring4d(geometry);
CREATE OR REPLACE FUNCTION substrate.geom_to_linestring4d(g geometry)
RETURNS public.linestring4d
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT public.array_to_linestring4d(
        ARRAY(
            SELECT v
            FROM ST_DumpPoints(g) AS d,
                 LATERAL (
                     VALUES
                         (COALESCE(ST_X(d.geom), 0)::DOUBLE PRECISION),
                         (COALESCE(ST_Y(d.geom), 0)::DOUBLE PRECISION),
                         (COALESCE(ST_Z(d.geom), 0)::DOUBLE PRECISION),
                         (COALESCE(ST_M(d.geom), 0)::DOUBLE PRECISION)
                 ) AS f(v)
            ORDER BY d.path, f.v   -- depth-first vertex order, 4 floats per vertex
        )
    );
$$;

COMMENT ON FUNCTION substrate.geom_to_linestring4d(geometry) IS
    'Walk one geometry depth-first into a flat (x,y,z,m) sequence packed as a native linestring4d. Used by dispatch arms that legitimately want the flat sequence (LINESTRINGZM trajectory, MULTIPOINTZM scatter). Callers needing subtype structure (POLYGON rings, MULTILINESTRING branches) must dispatch BEFORE calling this — flattening loses structure.';

-- ────────────────────────────────────────────────────────────────────────────
-- Helper: extract POLYGON exterior ring as a linestring4d. The exterior ring
-- IS the polygon's structural trajectory for Fréchet purposes. Holes (interior
-- rings) are placement metadata, not part of the boundary shape.
-- ────────────────────────────────────────────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.polygon_exterior_linestring4d(g geometry)
RETURNS public.linestring4d
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT substrate.geom_to_linestring4d(ST_ExteriorRing(g));
$$;

COMMENT ON FUNCTION substrate.polygon_exterior_linestring4d(geometry) IS
    'Extract a POLYGONZM''s exterior ring as a linestring4d for boundary-shape comparison. Interior rings (holes) are excluded — they are placement metadata, not boundary structure.';

-- ────────────────────────────────────────────────────────────────────────────
-- substrate.dist_4d(g1, g2) — primary subtype-dispatching distance.
-- Returns a meaningful number for every subtype × subtype pair. NULL only
-- when at least one operand is empty.
-- ────────────────────────────────────────────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.dist_4d(geometry, geometry);
CREATE OR REPLACE FUNCTION substrate.dist_4d(g1 geometry, g2 geometry)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    t1 TEXT := ST_GeometryType(g1);
    t2 TEXT := ST_GeometryType(g2);
BEGIN
    -- Fast path: POINT-vs-POINT pure 4D Euclidean.
    IF t1 = 'ST_Point' AND t2 = 'ST_Point' THEN
        RETURN public.distance_4d(
            public.point4d(ST_X(g1), ST_Y(g1), COALESCE(ST_Z(g1), 0), COALESCE(ST_M(g1), 0)),
            public.point4d(ST_X(g2), ST_Y(g2), COALESCE(ST_Z(g2), 0), COALESCE(ST_M(g2), 0)));
    END IF;

    -- Same-shape LINESTRING: discrete Fréchet on the trajectory.
    IF t1 = 'ST_LineString' AND t2 = 'ST_LineString' THEN
        RETURN public.frechet_4d(
            substrate.geom_to_linestring4d(g1),
            substrate.geom_to_linestring4d(g2));
    END IF;

    -- Same-shape POLYGON: Fréchet on the exterior rings (boundary shape).
    IF t1 = 'ST_Polygon' AND t2 = 'ST_Polygon' THEN
        RETURN public.frechet_4d(
            substrate.polygon_exterior_linestring4d(g1),
            substrate.polygon_exterior_linestring4d(g2));
    END IF;

    -- Same-shape MULTILINESTRING / MULTIPOLYGON: minimum component-pair
    -- Fréchet. Each branch / ring is a separate trajectory; cross-branch
    -- vertex concatenation would invent shape that isn't there.
    IF t1 IN ('ST_MultiLineString', 'ST_MultiPolygon') AND t2 = t1 THEN
        RETURN (
            SELECT MIN(public.frechet_4d(
                       substrate.geom_to_linestring4d(c1.geom),
                       substrate.geom_to_linestring4d(c2.geom)))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
        );
    END IF;

    -- MULTIPOINT-vs-MULTIPOINT: Hausdorff (Fréchet is undefined on unordered
    -- sets). Treats both inputs as scatter clouds.
    IF t1 = 'ST_MultiPoint' AND t2 = 'ST_MultiPoint' THEN
        RETURN public.hausdorff_4d(
            substrate.geom_to_linestring4d(g1),
            substrate.geom_to_linestring4d(g2));
    END IF;

    -- Cross-shape with at least one POINT: minimum 4D distance from the
    -- point to every vertex of the other geometry. Not Fréchet — that's
    -- not defined point-to-trajectory.
    IF t1 = 'ST_Point' THEN
        RETURN (
            SELECT MIN(public.distance_4d(
                       public.point4d(ST_X(g1), ST_Y(g1), COALESCE(ST_Z(g1), 0), COALESCE(ST_M(g1), 0)),
                       public.point4d(ST_X(d.geom), ST_Y(d.geom), COALESCE(ST_Z(d.geom), 0), COALESCE(ST_M(d.geom), 0))))
              FROM ST_DumpPoints(g2) d
        );
    END IF;
    IF t2 = 'ST_Point' THEN
        RETURN (
            SELECT MIN(public.distance_4d(
                       public.point4d(ST_X(d.geom), ST_Y(d.geom), COALESCE(ST_Z(d.geom), 0), COALESCE(ST_M(d.geom), 0)),
                       public.point4d(ST_X(g2), ST_Y(g2), COALESCE(ST_Z(g2), 0), COALESCE(ST_M(g2), 0))))
              FROM ST_DumpPoints(g1) d
        );
    END IF;

    -- GEOMETRYCOLLECTION on either side: dispatch component-by-component
    -- and return the minimum pairwise distance.
    IF t1 = 'ST_GeometryCollection' OR t2 = 'ST_GeometryCollection' THEN
        RETURN (
            SELECT MIN(substrate.dist_4d(c1.geom, c2.geom))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
        );
    END IF;

    -- Fallback: vertex-stream Fréchet. Triggered for combinations like
    -- LINESTRING-vs-POLYGON, MULTILINESTRING-vs-POLYGON, etc., where the
    -- structural answer is "compare boundary trajectories." Caller can
    -- dispatch differently if it needs a stricter shape semantic.
    RETURN public.frechet_4d(
        substrate.geom_to_linestring4d(g1),
        substrate.geom_to_linestring4d(g2));
END;
$$;

COMMENT ON FUNCTION substrate.dist_4d(geometry, geometry) IS
    'Subtype-dispatching 4D distance over GeometryZM. POINT/LINESTRING/POLYGON/MULTI*/COLLECTION pairs each route to the structurally appropriate native primitive (distance_4d, frechet_4d, hausdorff_4d, or component-wise minimum). Cross-shape pairs are explicitly handled. Substrate-side does no compute itself; libhartonomous via the C extension does the math.';

-- ────────────────────────────────────────────────────────────────────────────
-- substrate.frechet_4d_geom(g1, g2) — explicit Fréchet, subtype-aware.
-- Same dispatch principles as dist_4d but always returns a Fréchet value
-- (errors on subtype combinations where Fréchet is undefined, e.g. MULTIPOINT
-- — caller should use hausdorff_4d_geom instead).
-- ────────────────────────────────────────────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.frechet_4d_geom(geometry, geometry);
CREATE OR REPLACE FUNCTION substrate.frechet_4d_geom(g1 geometry, g2 geometry)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    t1 TEXT := ST_GeometryType(g1);
    t2 TEXT := ST_GeometryType(g2);
BEGIN
    IF t1 = 'ST_MultiPoint' OR t2 = 'ST_MultiPoint' THEN
        RAISE EXCEPTION 'frechet_4d_geom: Fréchet is undefined on MULTIPOINTZM (unordered set). Use substrate.hausdorff_4d_geom for scatter-cloud comparison.';
    END IF;

    IF t1 = 'ST_Polygon' AND t2 = 'ST_Polygon' THEN
        RETURN public.frechet_4d(
            substrate.polygon_exterior_linestring4d(g1),
            substrate.polygon_exterior_linestring4d(g2));
    END IF;

    IF t1 IN ('ST_MultiLineString', 'ST_MultiPolygon') AND t2 = t1 THEN
        RETURN (
            SELECT MIN(public.frechet_4d(
                       substrate.geom_to_linestring4d(c1.geom),
                       substrate.geom_to_linestring4d(c2.geom)))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
        );
    END IF;

    IF t1 = 'ST_GeometryCollection' OR t2 = 'ST_GeometryCollection' THEN
        RETURN (
            SELECT MIN(substrate.frechet_4d_geom(c1.geom, c2.geom))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
              WHERE ST_GeometryType(c1.geom) <> 'ST_MultiPoint'
                AND ST_GeometryType(c2.geom) <> 'ST_MultiPoint'
        );
    END IF;

    RETURN public.frechet_4d(
        substrate.geom_to_linestring4d(g1),
        substrate.geom_to_linestring4d(g2));
END;
$$;

COMMENT ON FUNCTION substrate.frechet_4d_geom(geometry, geometry) IS
    'Subtype-aware discrete Fréchet over GeometryZM. POLYGONZM uses exterior-ring trajectory; MULTI* uses minimum across component pairs; GEOMETRYCOLLECTIONZM dispatches per-component. Errors on MULTIPOINTZM (Fréchet undefined on unordered sets — use hausdorff_4d_geom).';

-- ────────────────────────────────────────────────────────────────────────────
-- substrate.hausdorff_4d_geom(g1, g2) — symmetric Hausdorff. Defined for all
-- subtype combinations including MULTIPOINTZM.
-- ────────────────────────────────────────────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.hausdorff_4d_geom(geometry, geometry);
CREATE OR REPLACE FUNCTION substrate.hausdorff_4d_geom(g1 geometry, g2 geometry)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    t1 TEXT := ST_GeometryType(g1);
    t2 TEXT := ST_GeometryType(g2);
BEGIN
    -- POLYGON: compare exterior rings.
    IF t1 = 'ST_Polygon' AND t2 = 'ST_Polygon' THEN
        RETURN public.hausdorff_4d(
            substrate.polygon_exterior_linestring4d(g1),
            substrate.polygon_exterior_linestring4d(g2));
    END IF;

    -- MULTI* same-shape: maximum across components (Hausdorff is a max-metric).
    IF t1 IN ('ST_MultiLineString', 'ST_MultiPolygon') AND t2 = t1 THEN
        RETURN (
            SELECT MAX(public.hausdorff_4d(
                       substrate.geom_to_linestring4d(c1.geom),
                       substrate.geom_to_linestring4d(c2.geom)))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
        );
    END IF;

    -- GEOMETRYCOLLECTION: dispatch per-component, take the maximum.
    IF t1 = 'ST_GeometryCollection' OR t2 = 'ST_GeometryCollection' THEN
        RETURN (
            SELECT MAX(substrate.hausdorff_4d_geom(c1.geom, c2.geom))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
        );
    END IF;

    -- Default (POINT, LINESTRING, MULTIPOINT, cross-shape): flatten and run
    -- native hausdorff_4d. Hausdorff tolerates flattening better than Fréchet
    -- because it's max-distance-of-min-distance over both sets.
    RETURN public.hausdorff_4d(
        substrate.geom_to_linestring4d(g1),
        substrate.geom_to_linestring4d(g2));
END;
$$;

COMMENT ON FUNCTION substrate.hausdorff_4d_geom(geometry, geometry) IS
    'Subtype-aware symmetric Hausdorff over GeometryZM. POLYGONZM uses exterior-ring; MULTI* takes maximum across component pairs (Hausdorff is a max-metric); GEOMETRYCOLLECTIONZM dispatches per-component. Defined for all subtypes including MULTIPOINTZM scatter clouds.';
