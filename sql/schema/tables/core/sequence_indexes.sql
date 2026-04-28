-- Inverse index: every parent that contains a given child entity at any
-- ordinal. Powers "every email that mentions noreply@example.com" and
-- "every document that quotes this text_composition" queries.
-- Created on the partitioned parent so PG creates matching indexes on every
-- partition automatically. Composite (child_type, child_hash) hits each
-- index at the leftmost prefix.
CREATE INDEX idx_sequence_child
    ON substrate.sequence (child_entity_type_id, child_entity_hash);

-- Range index for subtrajectory queries: "rows from ordinal M to N of
-- parent X". Partitioned PK (parent_type, parent_hash, ordinal) already
-- covers this — explicit index here for clarity in EXPLAIN output.
CREATE INDEX idx_sequence_parent_ordinal
    ON substrate.sequence (parent_entity_type_id, parent_entity_hash, ordinal);
