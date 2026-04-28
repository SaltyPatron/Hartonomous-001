CREATE TABLE substrate.schema_version (
    version    INT PRIMARY KEY,
    name       TEXT NOT NULL,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    checksum   TEXT NOT NULL
);
COMMENT ON TABLE substrate.schema_version IS
    'Applied migration ledger. Checksum is BLAKE3 of the migration''s expanded .up.sql content (after @include resolution).';
