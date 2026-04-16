\echo Use "CREATE EXTENSION hartonomous" to load this extension. \quit

-- Version
CREATE FUNCTION hartonomous_version()
RETURNS text
AS 'MODULE_PATHNAME', 'pg_hartonomous_version'
LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- BLAKE3
CREATE FUNCTION blake3_hash(bytea) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_blake3_hash'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION blake3_hash_text(text) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_blake3_hash_text'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

-- S3 Geometry (accept float8[] internally; SQL wrappers provide geometry interface)
CREATE FUNCTION _s3_distance(double precision[], double precision[]) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_s3_distance'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION _s3_centroid(double precision[][]) RETURNS double precision[]
    AS 'MODULE_PATHNAME', 'pg_s3_centroid'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION _super_fibonacci_project(double precision[]) RETURNS double precision[]
    AS 'MODULE_PATHNAME', 'pg_super_fibonacci_project'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION _hilbert_index(double precision[], int) RETURNS bigint
    AS 'MODULE_PATHNAME', 'pg_hilbert_index'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

-- Convenience wrappers using PostGIS geometry (POINTZM)
CREATE FUNCTION s3_distance(p1 geometry, p2 geometry) RETURNS double precision AS $$
    SELECT _s3_distance(
        ARRAY[ST_X(p1), ST_Y(p1), ST_Z(p1), ST_M(p1)],
        ARRAY[ST_X(p2), ST_Y(p2), ST_Z(p2), ST_M(p2)]
    );
$$ LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION super_fibonacci_project(params double precision[]) RETURNS geometry AS $$
    SELECT ST_MakePoint(r[1], r[2], r[3], r[4])
    FROM _super_fibonacci_project(params) AS r;
$$ LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION hilbert_index(point geometry) RETURNS bigint AS $$
    SELECT _hilbert_index(
        ARRAY[ST_X(point), ST_Y(point), ST_Z(point), ST_M(point)],
        8
    );
$$ LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION hilbert_index(point geometry, "order" int) RETURNS bigint AS $$
    SELECT _hilbert_index(
        ARRAY[ST_X(point), ST_Y(point), ST_Z(point), ST_M(point)],
        "order"
    );
$$ LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE;

-- Graph Traversal (SPI-based, reads substrate tables)
CREATE TYPE neighbors_result AS (
    entity_id       bigint,
    edge_id         bigint,
    edge_type_id    int,
    depth           int,
    path            bigint[]
);

CREATE TYPE traversal_path AS (
    target_entity_id    bigint,
    cost                double precision,
    path                bigint[],
    edge_path           bigint[]
);

CREATE FUNCTION neighbors(bigint, int DEFAULT NULL, int DEFAULT 1)
    RETURNS SETOF neighbors_result
    AS 'MODULE_PATHNAME', 'pg_neighbors'
    LANGUAGE C STABLE PARALLEL SAFE ROWS 100;

CREATE FUNCTION traverse_astar(bigint, int, int, int DEFAULT 5, int DEFAULT 100)
    RETURNS SETOF traversal_path
    AS 'MODULE_PATHNAME', 'pg_traverse_astar'
    LANGUAGE C STABLE PARALLEL SAFE ROWS 100;
