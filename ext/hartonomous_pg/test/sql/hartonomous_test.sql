-- pg_regress test for hartonomous extension
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS hartonomous;

-- Version
SELECT length(hartonomous_version()) > 0 AS has_version;

-- BLAKE3
SELECT length(blake3_hash('\x48656c6c6f'::bytea)) AS blake3_len;
SELECT length(blake3_hash_text('hello')) AS blake3_text_len;
SELECT blake3_hash_text('hello') = blake3_hash_text('hello') AS blake3_deterministic;
SELECT blake3_hash_text('hello') != blake3_hash_text('world') AS blake3_distinct;

-- S3 Distance
SELECT round(_s3_distance(
    ARRAY[1.0, 0.0, 0.0, 0.0],
    ARRAY[1.0, 0.0, 0.0, 0.0]
)::numeric, 6) AS s3_self_distance;

SELECT round(_s3_distance(
    ARRAY[1.0, 0.0, 0.0, 0.0],
    ARRAY[-1.0, 0.0, 0.0, 0.0]
)::numeric, 4) AS s3_antipodal;

SELECT round(_s3_distance(
    ARRAY[1.0, 0.0, 0.0, 0.0],
    ARRAY[0.0, 1.0, 0.0, 0.0]
)::numeric, 4) AS s3_orthogonal;

-- S3 Distance via geometry wrapper
SELECT round(s3_distance(
    ST_MakePoint(1.0, 0.0, 0.0, 0.0),
    ST_MakePoint(0.0, 1.0, 0.0, 0.0)
)::numeric, 4) AS s3_geom_distance;

-- Super-Fibonacci
SELECT array_length(_super_fibonacci_project(ARRAY[0.0, 100.0]), 1) AS superfib_dims;

-- Super-Fibonacci deterministic
SELECT _super_fibonacci_project(ARRAY[42.0, 1000.0]) = _super_fibonacci_project(ARRAY[42.0, 1000.0]) AS superfib_deterministic;

-- Hilbert index
SELECT _hilbert_index(ARRAY[0.0, 0.0, 0.0, 0.0], 4) AS hilbert_origin;
SELECT _hilbert_index(ARRAY[0.5, 0.5, 0.5, 0.5], 4) AS hilbert_center;

-- Hilbert round-trip via geometry wrapper
SELECT hilbert_index(ST_MakePoint(0.5, 0.5, 0.5, 0.5)) AS hilbert_geom;

-- Hilbert order matters
SELECT _hilbert_index(ARRAY[0.5, 0.5, 0.5, 0.5], 4) != _hilbert_index(ARRAY[0.5, 0.5, 0.5, 0.5], 8) AS hilbert_order_matters;

-- Error cases
DO $$
BEGIN
    PERFORM _s3_distance(ARRAY[1.0, 0.0], ARRAY[1.0, 0.0, 0.0, 0.0]);
    RAISE EXCEPTION 'should have failed';
EXCEPTION WHEN array_element_error THEN
    RAISE NOTICE 's3_distance correctly rejects non-4D input';
END $$;

DO $$
BEGIN
    PERFORM _hilbert_index(ARRAY[0.5, 0.5, 0.5, 0.5], 20);
    RAISE EXCEPTION 'should have failed';
EXCEPTION WHEN numeric_value_out_of_range THEN
    RAISE NOTICE 'hilbert_index correctly rejects order > 16';
END $$;

-- Done
SELECT 'all tests passed' AS result;
