-- 0049_substrate_4d_operators.down.sql
DROP FUNCTION IF EXISTS substrate.st_4d_distance_to_centroid(geometry, geometry[]);
DROP FUNCTION IF EXISTS substrate.st_4d_hausdorff_distance(geometry, geometry);
DROP FUNCTION IF EXISTS substrate.st_4d_frechet_distance(geometry, geometry);
DROP AGGREGATE IF EXISTS substrate.st_s3_centroid(geometry);
DROP AGGREGATE IF EXISTS substrate.st_4d_centroid(geometry);
DROP FUNCTION IF EXISTS substrate.st_s3_centroid_finalfunc(float8[]);
DROP FUNCTION IF EXISTS substrate.st_4d_centroid_finalfunc(float8[]);
DROP FUNCTION IF EXISTS substrate.st_4d_centroid_sfunc(float8[], geometry);
DROP FUNCTION IF EXISTS substrate.st_4d_normalize(geometry);
DROP FUNCTION IF EXISTS substrate.st_4d_norm(geometry);
DROP FUNCTION IF EXISTS substrate.st_4d_dot(geometry, geometry);
DROP FUNCTION IF EXISTS substrate.st_s3_distance(geometry, geometry);
DROP FUNCTION IF EXISTS substrate.st_4d_distance(geometry, geometry);
