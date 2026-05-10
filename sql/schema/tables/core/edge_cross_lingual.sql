-- Partition for cross_lingual edge_types (IDs 16..29 per sql/schema/seed/edge_type.sql).
-- Translation, etymology, and language-name relations across language boundaries.
CREATE TABLE substrate.edge_cross_lingual
    PARTITION OF substrate.edge FOR VALUES IN (16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29);
