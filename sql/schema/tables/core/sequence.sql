-- substrate.sequence — the indexed parent → ordered children record.
--
-- Hash-as-PK throughout. Composite (parent_hash, ordinal) is the natural
-- key — repetition (refrain in Green Eggs and Ham, noreply@example.com
-- appearing 47 times in one email body) is preserved by distinct ordinals
-- pointing to the SAME content-addressed child entity. The child entity
-- stays one row in substrate.entity (content dedup); the sequence rows
-- are how we record where that one entity sits inside each parent.
--
-- rle_count compresses contiguous runs of the same child: three identical
-- sentences in a row collapse to one row with ordinal = first position
-- and rle_count = 3. Lookup at ordinal N walks
-- WHERE ordinal <= N AND ordinal + rle_count > N — still indexed,
-- still microseconds.
--
-- Per-entity-type partitioning DROPPED: substrate.entity is no longer
-- partitioned by type (Phase C of unification refactor — entity is
-- content-only; types are junction metadata). Sequence is similarly
-- single-table now. Index on (parent_hash, ordinal) provides O(log N)
-- random access; inverse index on (child_hash) provides parent lookup.
CREATE TABLE substrate.sequence (
    parent_hash substrate.hash_value NOT NULL,
    ordinal     INT  NOT NULL,
    child_hash  substrate.hash_value NOT NULL,
    rle_count   INT  NOT NULL DEFAULT 1,
    PRIMARY KEY (parent_hash, ordinal)
    -- FK to substrate.entity intentionally omitted — application-layer
    -- batch ordering guarantees parent + child entity rows exist before
    -- their sequence rows. (Same PG18.3 partitionwise-FK SEGV pattern
    -- documented elsewhere; conservatively kept omitted post-collapse.)
);

CREATE INDEX idx_sequence_child ON substrate.sequence(child_hash, parent_hash);

COMMENT ON TABLE substrate.sequence IS
    'Parent → ordered children with RLE for refrain compression. Hash-only references — entity type is irrelevant to ordinal lookup. Btree-indexed on (parent_hash, ordinal) for microsecond random access; inverse index on (child_hash) for parent lookup.';
