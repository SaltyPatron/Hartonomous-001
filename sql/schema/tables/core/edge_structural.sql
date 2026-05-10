-- Partition for structural edge_types (IDs 1..15 per sql/schema/seed/edge_type.sql).
-- Within-modality structural composition for the text stack.
CREATE TABLE substrate.edge_structural
    PARTITION OF substrate.edge FOR VALUES IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
