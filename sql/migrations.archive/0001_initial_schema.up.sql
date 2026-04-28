-- Migration 0001: Initial schema
-- Creates substrate and monitor schemas, schema_version tracking table.

CREATE SCHEMA IF NOT EXISTS substrate;
CREATE SCHEMA IF NOT EXISTS monitor;

CREATE TABLE substrate.schema_version (
    version    INT PRIMARY KEY,
    name       TEXT NOT NULL,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    checksum   TEXT NOT NULL
);

COMMENT ON TABLE substrate.schema_version IS
    'Applied migration ledger. Checksum is SHA-256 of the .up.sql file content at apply time.';
