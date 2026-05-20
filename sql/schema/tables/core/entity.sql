-- substrate.entity is content-addressed: same BLAKE3 hash → same row.
-- Identity ONLY. Hash PK; no other columns. Classifications go on
-- substrate.entity_classification. Geometry lives on substrate.physicality.
--
-- HASH-partitioned on `hash` over 8 children (entity_p0..entity_p7). PG18's
-- native hash partitioning routes rows by its internal hashing function on
-- the bytea hash column; the PK on `hash` alone works because the partition
-- key is the bare column (no expression).
--
-- Vertex reverse-resolve from content-tier LINESTRINGZM mantissa slots back
-- to full entity hashes is served by the FUNCTIONAL INDEX
-- `entity_hash_prefix_idx` defined in sql/schema/indexes/. The index uses
-- substrate.bb_hash_lo(hash) / substrate.bb_hash_hi(hash) expression columns
-- — no per-row generated-column storage bloat; PG evaluates the expressions
-- during scan and matches against the probe values supplied by
-- substrate.entity_by_hash_prefix(BIGINT[], BIGINT[]).
--
-- Substrate's 4D realization lives in substrate.physicality, partitioned
-- by physicality_type_id. Atom POINTZMs (codepoint S^3 via blob, audio
-- sample, pixel intensity), composition LINESTRINGZM through children's
-- centroids, content-tier mantissa-packed LINESTRINGZM through entity hash
-- refs — all live there, not here.
CREATE TABLE substrate.entity (
    hash substrate.hash_value PRIMARY KEY
) PARTITION BY HASH (hash);

COMMENT ON TABLE substrate.entity IS
    'Content-addressed substrate nodes. Identity = BLAKE3 hash of content; hash is the PK and ONLY column. Geometry lives on substrate.physicality. HASH-partitioned over 8 children entity_p0..entity_p7 — PG18 native hash partitioning on the bare hash column. Classifications live on substrate.entity_classification. Vertex reverse-resolve from content-tier LINESTRINGZM mantissas uses the functional btree entity_hash_prefix_idx on (substrate.bb_hash_lo(hash), substrate.bb_hash_hi(hash)).';
