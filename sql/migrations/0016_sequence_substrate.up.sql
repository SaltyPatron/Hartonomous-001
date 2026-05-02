-- Stage 0016: substrate.sequence — the indexed parent → ordered children
-- record that gives the substrate microsecond random access by ordinal.
--
-- Replaces the has_constituent edge introduced in 0015 (an antipattern: the
-- edge_member PK has no ordinal column, so repeated children — refrain in
-- "Green Eggs and Ham", noreply@example.com appearing 47× in one email —
-- collapsed into a single edge_member row, losing the count). The
-- LINESTRINGZM physicality on a parent stays as the geometric truth for
-- similarity queries (Fréchet, Hausdorff); substrate.sequence is the
-- complementary identity-and-ordinal record that powers:
--
--   substrate.composition_at(parent, n)            — what's at position N
--   substrate.composition_before/after             — neighbors
--   substrate.composition_range(parent, m, n)      — sub-range
--   substrate.composition_subtrajectory(parent, …) — sub-LINESTRINGZM
--   substrate.composition_parents(child)           — inverse: where is X?
--   substrate.recompose_text(root, depth)          — byte-perfect rebuild
--
-- All hash-as-PK. All btree-indexed lookups. All RLE-aware (a row with
-- rle_count=R covers ordinal..ordinal+R-1, all pointing to the same child).
-- Partitioned by parent_entity_type_id mirroring substrate.entity.

-- ── Drop the antipattern from 0015 ──────────────────────────────────
DROP FUNCTION IF EXISTS substrate.recompose_text(INT, BYTEA, INT);
DROP FUNCTION IF EXISTS substrate.get_composition_children(INT, BYTEA);
DELETE FROM substrate.edge_type WHERE code = 'has_constituent';

-- ── substrate.sequence — single hash-keyed table (post-Phase-C) ────
-- @include schema/tables/core/sequence.sql

-- ── Query surface ───────────────────────────────────────────────────
-- @include schema/functions/composition_at.sql
-- @include schema/functions/composition_before.sql
-- @include schema/functions/composition_after.sql
-- @include schema/functions/composition_range.sql
-- @include schema/functions/composition_subtrajectory.sql
-- @include schema/functions/composition_parents.sql
-- @include schema/functions/get_composition_children.sql

-- ── Recompose via sequence walk (replaces 0015's has_constituent walk) ──
-- @include schema/functions/recompose_text_v2.sql

-- ── Per-batch flush ────────────────────────────────────────────────
-- @include schema/functions/flush_sequence_from_staging.sql
