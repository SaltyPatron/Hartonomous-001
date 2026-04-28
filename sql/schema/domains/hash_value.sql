CREATE DOMAIN substrate.hash_value AS BYTEA
    CONSTRAINT hash_value_length CHECK (octet_length(VALUE) = 32);
COMMENT ON DOMAIN substrate.hash_value IS
    'BLAKE3 256-bit hash. The substrate''s only identity surface — entities and edges are keyed on (type_id, hash_value).';
