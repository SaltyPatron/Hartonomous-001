-- Composite btree on (hash_bits_0_51, hash_bits_52_103). The read-side kernel
-- of SubstrateTierWalker: substrate.entity_by_hash_prefix(BIGINT[], BIGINT[])
-- resolves trajectory-vertex (X, Z) mantissa slices to full BLAKE3 hashes in
-- one batched point lookup per tier. Without this index the lookup falls
-- back to a sequential scan over substrate.entity, defeating the whole
-- O(D)-tier-walks contract.
CREATE INDEX IF NOT EXISTS entity_hash_prefix_idx
    ON substrate.entity USING btree (hash_bits_0_51, hash_bits_52_103);
