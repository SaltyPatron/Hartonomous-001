CREATE SCHEMA IF NOT EXISTS substrate;
COMMENT ON SCHEMA substrate IS
    'Content-addressed substrate. Every table here is keyed on BLAKE3 hashes; no surrogate IDs.';
