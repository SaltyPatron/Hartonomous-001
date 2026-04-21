-- Migration 0027: Fix sequence table deduplication
-- The pipeline's CreateSequencesAsync was inserting without ON CONFLICT,
-- so multiple decomposers touching the same entity created duplicate
-- sequence rows (e.g. "dog" lemma got 5 copies of its codepoint sequence).
--
-- This migration:
--   1. Removes duplicate sequence rows, keeping the lowest id per (parent_id, ordinal_position).
--   2. Adds a UNIQUE constraint to prevent future duplicates.

-- Step 1: Delete duplicates. Keep the row with the lowest ctid for each (parent_id, ordinal_position).
DELETE FROM substrate.sequence s
WHERE s.ctid NOT IN (
    SELECT MIN(s2.ctid)
    FROM substrate.sequence s2
    GROUP BY s2.parent_id, s2.ordinal_position
);

-- Step 2: Add UNIQUE constraint so ON CONFLICT works in the pipeline.
ALTER TABLE substrate.sequence
    ADD CONSTRAINT uq_sequence_parent_position UNIQUE (parent_id, ordinal_position);
