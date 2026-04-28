-- Migration 0001 DOWN: Drop substrate and monitor schemas.

DROP SCHEMA IF EXISTS monitor CASCADE;
DROP SCHEMA IF EXISTS substrate CASCADE;
