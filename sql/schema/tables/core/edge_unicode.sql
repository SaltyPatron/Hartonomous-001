-- Partition for unicode edge_types (IDs 32..34 per sql/schema/seed/edge_type.sql).
-- Codepoint-level Unicode tables (lowercase mapping, case-folding, collation).
CREATE TABLE substrate.edge_unicode
    PARTITION OF substrate.edge FOR VALUES IN (32, 33, 34);
