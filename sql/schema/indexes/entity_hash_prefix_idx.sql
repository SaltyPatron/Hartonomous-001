-- Composite FUNCTIONAL btree on (bb_hash_lo(hash), bb_hash_hi(hash)). The
-- read-side kernel of SubstrateTierWalker: substrate.entity_by_hash_prefix
-- (BIGINT[], BIGINT[]) resolves content-tier LINESTRINGZM vertex (X, Z)
-- mantissa slices to full BLAKE3 hashes in one batched point lookup per tier.
-- Without this index the lookup falls back to a sequential scan over
-- substrate.entity, defeating the whole O(tier-depth) reverse-resolve contract.
--
-- Functional index avoids the 16-byte-per-row generated-column bloat of
-- storing hash_bits_0_51 + hash_bits_52_103 on every entity row — PG
-- evaluates the bb_hash_lo / bb_hash_hi expressions during scan and matches
-- against probe values. Same lookup performance, zero extra row storage.
CREATE INDEX IF NOT EXISTS entity_hash_prefix_idx
    ON substrate.entity USING btree (substrate.bb_hash_lo(hash), substrate.bb_hash_hi(hash));
