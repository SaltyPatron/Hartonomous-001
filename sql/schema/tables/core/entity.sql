-- Entity is PURELY content-addressed: same content → same BLAKE3 hash →
-- same row. Period. Identity is the hash, not (type, hash). Classifications
-- ("this content is a word_form" / "this content is a lemma") live on
-- substrate.entity_classification, not in the entity's identity.
--
-- This is the substrate's invention rule: "dog" is "dog" regardless of
-- semantic role. Whether a decomposer USES this content as a word_form,
-- lemma, codepoint, grapheme_cluster, audio_recording, pixel_region, or
-- any other classification is metadata about how the entity is consumed,
-- not about what it IS.
--
-- No partitioning by type. The entity table is a single index of hashes;
-- B-tree on the PK gives O(log N) lookup. Per-type query patterns now
-- JOIN substrate.entity_classification instead of partition-pruning.
--
-- hash_bits_0_51 + hash_bits_52_103 expose a 104-bit BLAKE3-derived prefix
-- as two BIGINT columns. Used for two purposes:
--   1. Reverse-resolving a composition LINESTRINGZM vertex back to its
--      child entity: each vertex (X, Z) mantissa carries the child's
--      hash prefix (X = hash_bits_0_51, Z = hash_bits_52_103) via the
--      bb_pack_hash_lo / bb_pack_hash_hi encoding. Unpacking a vertex and
--      joining against the (hash_bits_0_51, hash_bits_52_103) composite
--      btree (entity_hash_prefix_idx) recovers the child hash in one
--      indexed point lookup — no junction table required.
--   2. Batched lookups via substrate.entity_by_hash_prefix(BIGINT[],
--      BIGINT[]) for any caller that has hash prefixes in hand.
--
-- The expressions are inlined here (rather than calling substrate.bb_hash_lo
-- / bb_hash_hi) because GENERATED ALWAYS AS STORED requires the expression
-- to be evaluable at CREATE TABLE time, and the bb_* function definitions
-- live in the Phase 13 functions block. The two encodings are byte-for-byte
-- equivalent: any change to bb_hash_lo / bb_hash_hi must mirror here.
--
-- entity carries NO geometry column. The substrate's 4D realization for an
-- entity lives in substrate.physicality, partitioned by physicality_type_id
-- (s3_position for codepoint atoms, contour for compositions, etc.). The
-- prior Bite-A centroid_4d column on this table bypassed the partitioned
-- physicality store and is removed.
CREATE TABLE substrate.entity (
    hash substrate.hash_value PRIMARY KEY,
    hash_bits_0_51 BIGINT GENERATED ALWAYS AS (
          (get_byte(hash, 0)::BIGINT)
        | (get_byte(hash, 1)::BIGINT << 8)
        | (get_byte(hash, 2)::BIGINT << 16)
        | (get_byte(hash, 3)::BIGINT << 24)
        | (get_byte(hash, 4)::BIGINT << 32)
        | (get_byte(hash, 5)::BIGINT << 40)
        | ((get_byte(hash, 6) & 15)::BIGINT << 48)
    ) STORED,
    hash_bits_52_103 BIGINT GENERATED ALWAYS AS (
          ((get_byte(hash, 6) >> 4) & 15)::BIGINT
        | (get_byte(hash, 7)::BIGINT << 4)
        | (get_byte(hash, 8)::BIGINT << 12)
        | (get_byte(hash, 9)::BIGINT << 20)
        | (get_byte(hash, 10)::BIGINT << 28)
        | (get_byte(hash, 11)::BIGINT << 36)
        | (get_byte(hash, 12)::BIGINT << 44)
    ) STORED
);

COMMENT ON TABLE substrate.entity IS
    'Content-addressed substrate nodes. Atom OR composition. Identity = BLAKE3 hash of content. Classifications via substrate.entity_classification. Single table — no LIST partition by type. hash_bits_0_51 / hash_bits_52_103 expose a 104-bit BLAKE3 prefix as BIGINT columns so composition-LINESTRINGZM vertex (X, Z) mantissas resolve to full hashes via the composite-btree composite index (entity_hash_prefix_idx). No geometry column — physicality lives in substrate.physicality, partitioned by physicality_type_id.';

COMMENT ON COLUMN substrate.entity.hash_bits_0_51 IS
    'Bits 0..51 of substrate.entity.hash, LE byte order, exposed as BIGINT. Mirrors substrate.bb_hash_lo(bytea). Matches the X mantissa of composition LINESTRINGZM vertices and the X mantissa of edge.geom vertices via substrate.bb_pack_hash_lo. Used for batched lookup via substrate.entity_by_hash_prefix.';

COMMENT ON COLUMN substrate.entity.hash_bits_52_103 IS
    'Bits 52..103 of substrate.entity.hash, LE byte order, exposed as BIGINT. Mirrors substrate.bb_hash_hi(bytea). Matches the Z mantissa of composition LINESTRINGZM vertices and the Z mantissa of edge.geom vertices via substrate.bb_pack_hash_hi.';
