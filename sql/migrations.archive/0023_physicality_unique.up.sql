-- 0023_physicality_unique.up.sql
-- Physicality becomes content-addressed, matching the substrate's core law.
--
-- Before this migration, substrate.physicality had no way to dedupe. The PK
-- (id, physicality_type_id) is on a BIGSERIAL, so it cannot catch duplicate
-- geometries. Re-running any decomposer appended fresh rows.
--
-- A single entity legitimately has MANY physicalities:
--   • different types: a codepoint has s3_position AND hilbert_value;
--   • same type, different frames: a bpe_token has an embedding_firefly per
--     source model — BERT's firefly and GPT-4's firefly occupy different S3
--     positions but both belong to the same content-hashed token entity.
--
-- Therefore the invariant is NOT one-row-per-(entity, type). It's the same
-- law entities obey: content-addressed. Each physicality row carries BLAKE3
-- of its WKB. Identity is (entity_id, physicality_type_id, content_hash).
-- Re-emission of identical geometry dedupes. A different geometry for the
-- same (entity, type) — e.g., a second model's firefly — is a new row.
-- Byte-identical re-runs produce byte-identical substrate state (Law #6).

-- The C# ingestion pipeline computes BLAKE3 of the WKB and supplies it on
-- insert. No SQL-side hashing — BLAKE3 isn't a PG builtin, and putting it
-- behind an extension would couple the migration to native-lib loading.

-- Dedupe any rows that already collide under the new invariant.
DELETE FROM substrate.physicality p
USING (
    SELECT id, physicality_type_id,
           row_number() OVER (
               PARTITION BY entity_id, physicality_type_id, ST_AsBinary(geom)
               ORDER BY id
           ) AS rn
    FROM substrate.physicality
) d
WHERE p.id = d.id
  AND p.physicality_type_id = d.physicality_type_id
  AND d.rn > 1;

ALTER TABLE substrate.physicality
    ADD COLUMN content_hash BYTEA;

-- Seed existing rows. WKB bytes are the canonical content; any stable 32-byte
-- hash of those bytes is acceptable here because these rows predate the
-- invariant and will be replaced on the next re-run. pgcrypto's sha256 is
-- available in stock postgis images and produces stable 32-byte output.
-- Fresh ingestion from C# supplies true BLAKE3 for all new rows.
CREATE EXTENSION IF NOT EXISTS pgcrypto;
UPDATE substrate.physicality
SET content_hash = digest(ST_AsBinary(geom), 'sha256')
WHERE content_hash IS NULL;

ALTER TABLE substrate.physicality
    ALTER COLUMN content_hash SET NOT NULL;

ALTER TABLE substrate.physicality
    ADD CONSTRAINT physicality_content_uk
    UNIQUE (entity_id, physicality_type_id, content_hash);

CREATE INDEX IF NOT EXISTS idx_physicality_entity_type_hash
    ON substrate.physicality (entity_id, physicality_type_id, content_hash);

COMMENT ON COLUMN substrate.physicality.content_hash IS
    'BLAKE3 of ST_AsBinary(geom). Content-addresses the geometry — identical re-emissions dedupe, different geometries for the same (entity, type) coexist (e.g., per-model fireflies).';
COMMENT ON CONSTRAINT physicality_content_uk ON substrate.physicality IS
    'Content-addressed uniqueness. An entity can have many physicalities per type; the same geometry appears exactly once — Law #6 for physicality.';
