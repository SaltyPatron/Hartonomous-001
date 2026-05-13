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
-- The composite (entity_type_id, hash) PK that previously fragmented
-- "dog the lemma" and "dog the word_form" into TWO rows is gone. One hash
-- = one row. Period.
--
-- No partitioning by type. The entity table is a single index of hashes;
-- B-tree on the PK gives O(log N) lookup. Per-type query patterns now
-- JOIN substrate.entity_classification instead of partition-pruning.
--
-- hash_bits_0_51 + hash_bits_52_103 expose a 104-bit BLAKE3-derived prefix
-- as two BIGINT columns, so trajectory-vertex (X, Z) mantissas — the X+Z
-- 52-bit halves of each ingestion_trajectory LINESTRING4D vertex — can
-- resolve to full hashes through a single batched composite-btree point
-- lookup (substrate.entity_by_hash_prefix(BIGINT[], BIGINT[])).
--
-- The expressions are inlined here (rather than calling substrate.bb_hash_lo
-- / bb_hash_hi) because GENERATED ALWAYS AS STORED requires the expression
-- to be evaluable at CREATE TABLE time, and the bb_* function definitions
-- live in the Phase 13 functions block. The two encodings are byte-for-byte
-- equivalent: any change to bb_hash_lo / bb_hash_hi must mirror here.
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
    ) STORED,
    -- Universal 4D representative POINTZM for this entity. For atoms, the
    -- real content-derived centroid (codepoint S^3 Super-Fibonacci by UCA
    -- rank; audio sample value at (time, freq, mag, phase); image pixel at
    -- (x, y, intensity, class); etc.). For compositions, the recursive mean
    -- of children's centroid_4d values — computed at INSERT time by the
    -- ingestion pipeline.
    --
    -- Drives:
    --   * edge.geom construction (LINESTRINGZM through participants' centroid_4d
    --     in role order) — substrate.populate_edge_trajectories reads this
    --   * recursive Merkle centroid math up the composition tier ladder
    --   * GiST k-NN neighborhood queries (codepoint clusters, embedding
    --     fireflies, etc.) via the gist_geometry_ops_nd index below
    --
    -- Compositions store their child-sequence in physicality.geom as
    -- ID-encoded LINESTRINGZM (mantissa-packed vertices); this column
    -- carries the real-coord position for cross-tier and cross-edge math.
    centroid_4d geometry(POINTZM) NOT NULL
);

CREATE INDEX entity_centroid_4d_idx
    ON substrate.entity USING gist (centroid_4d gist_geometry_ops_nd);

COMMENT ON TABLE substrate.entity IS
    'Content-addressed substrate nodes. Atom OR composition. Identity = BLAKE3 hash of content. Classifications via substrate.entity_classification. Single table — no LIST partition by type. hash_bits_0_51 / hash_bits_52_103 expose a 104-bit BLAKE3 prefix as BIGINT columns so trajectory-vertex X+Z mantissas resolve to full hashes via the composite-btree composite index (entity_hash_prefix_idx). centroid_4d is the universal 4D representative POINTZM (real coords) — drives edge.geom population, recursive Merkle centroid math, and GiST k-NN spatial queries.';

COMMENT ON COLUMN substrate.entity.hash_bits_0_51 IS
    'Bits 0..51 of substrate.entity.hash, LE byte order, exposed as BIGINT. Mirrors substrate.bb_hash_lo(bytea). Lower half of the 104-bit hash prefix used for trajectory-vertex X mantissa packing and for batched lookup via substrate.entity_by_hash_prefix.';

COMMENT ON COLUMN substrate.entity.hash_bits_52_103 IS
    'Bits 52..103 of substrate.entity.hash, LE byte order, exposed as BIGINT. Mirrors substrate.bb_hash_hi(bytea). Upper half of the 104-bit hash prefix used for trajectory-vertex Z mantissa packing.';

COMMENT ON COLUMN substrate.entity.centroid_4d IS
    'Real-coord 4D representative position. Atoms: content-derived centroid (codepoint S^3 by UCA rank, audio frame coords, image pixel coords, etc.). Compositions: recursive mean of children''s centroid_4d. Computed at INSERT time by the ingestion pipeline; drives edge.geom + recursive Merkle math + spatial neighborhood queries.';
