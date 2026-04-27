-- 0061_embedding_alignment_anchor.down.sql

DROP FUNCTION IF EXISTS substrate.get_firefly_coords(BIGINT[], BIGINT);
DROP FUNCTION IF EXISTS substrate.apply_firefly_rotation(
    BIGINT, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8);
DROP FUNCTION IF EXISTS substrate.claim_or_get_embedding_anchor(BIGINT, INT);
DROP TABLE IF EXISTS substrate.embedding_alignment_anchor;
