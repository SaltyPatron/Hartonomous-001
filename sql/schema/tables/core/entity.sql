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
CREATE TABLE substrate.entity (
    hash substrate.hash_value PRIMARY KEY
);

COMMENT ON TABLE substrate.entity IS
    'Content-addressed substrate nodes. Atom OR composition. Identity = BLAKE3 hash of content. Classifications via substrate.entity_classification. Single table — no LIST partition by type.';
