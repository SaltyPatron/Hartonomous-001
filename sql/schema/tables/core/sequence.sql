-- substrate.sequence — the indexed parent → ordered children record.
--
-- Reconstruction substrate. The LINESTRINGZM physicality on a parent gives
-- you geometric truth for similarity (Fréchet, Hausdorff, frayed edges); the
-- sequence table gives you O(log N) random access by ordinal for the
-- queries the invention names:
--
--   "what's at position N of parent X?"     → btree lookup, microseconds
--   "what came before/after position N?"    → ordinal ± 1 lookup
--   "subtrajectory from M to N"             → range scan
--   "every parent that contains this child" → inverse index on (child)
--
-- Hash-as-PK throughout. Composite (parent_type_id, parent_hash, ordinal)
-- is the natural key — repetition (refrain in Green Eggs and Ham,
-- noreply@example.com appearing 47 times in one email body) is preserved
-- by distinct ordinals pointing to the SAME content-addressed child entity.
-- The child entity stays one row in substrate.entity (content dedup); the
-- sequence rows are how we record where that one entity sits inside each
-- parent.
--
-- rle_count compresses contiguous runs of the same child: three identical
-- sentences in a row collapse to one row with ordinal = first position and
-- rle_count = 3. Lookup at ordinal N walks WHERE ordinal <= N AND
-- ordinal + rle_count > N — still indexed, still microseconds.
--
-- Partitioned by parent_entity_type_id (LIST), mirroring substrate.entity's
-- partition strategy. Each partition holds the sequence rows for parents of
-- one entity type — text_composition's sequence rows live in their own
-- partition, document's in theirs, etc. — so ordered walks of a parent
-- never cross-touch unrelated partitions.
CREATE TABLE substrate.sequence (
    parent_entity_type_id INT  NOT NULL,
    parent_entity_hash    substrate.hash_value NOT NULL,
    ordinal               INT  NOT NULL,
    child_entity_type_id  INT  NOT NULL,
    child_entity_hash     substrate.hash_value NOT NULL,
    rle_count             INT  NOT NULL DEFAULT 1,
    PRIMARY KEY (parent_entity_type_id, parent_entity_hash, ordinal)
    -- Composite FKs to substrate.entity intentionally omitted: PG18.3
    -- partitionwise FK validation crashes under bulk INSERT (same pattern
    -- as edge_member, physicality). Pipeline batch ordering guarantees
    -- entities exist before their sequence rows.
) PARTITION BY LIST (parent_entity_type_id);

COMMENT ON TABLE substrate.sequence IS
    'Parent → ordered children with RLE for refrain compression. Btree-indexed (parent_type_id, parent_hash, ordinal) for microsecond random access. Inverse-indexed on (child) for parent lookup. The LINESTRINGZM physicality is for geometric queries; this table is for ordinal queries.';
