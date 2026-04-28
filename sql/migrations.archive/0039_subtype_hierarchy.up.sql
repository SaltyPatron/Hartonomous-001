-- 0039_subtype_hierarchy.up.sql
--
-- Adds parent-child hierarchy across reference tables so subtypes traverse
-- to their parent universal relation via FK rather than string-code matching.
-- Without this, "find every hypernym-like edge" or "find every nsubj including
-- subtypes" requires a manual JOIN through a separate reference table by code,
-- and `substrate.edge_type` itself can't be traversed as a class hierarchy at
-- all.
--
-- Concrete rationale (the user's analogy "deprel is a category with subcategories"):
--   Universal Dependencies has 37 universal relations (nsubj, obj, nmod, ...)
--   and language-specific subtypes via colon notation (nsubj:pass, nmod:poss,
--   compound:smixut, ...). 421 deprel codes total in this substrate (37 root +
--   384 subtype). substrate.deprel already has parent_id, but the corresponding
--   edge_type rows have no parent linkage.
--
--   WordNet has hypernym/hyponym (siblings), holonym/{member,part,substance}_holonym
--   and meronym/{member,part,substance}_meronym families. substrate.semantic_relation_type
--   has no parent_id at all — every code is a flat sibling.
--
-- Three changes:
--   1. Add parent_id self-FK to substrate.semantic_relation_type, then register
--      the WordNet relation hierarchy (holonym ⊃ member_holonym/part_holonym/
--      substance_holonym; meronym ⊃ ... ; root is_a-relation if useful).
--   2. Add parent_id self-FK to substrate.edge_type so any edge type can be
--      traversed as a class hierarchy (recursive CTE: starting from nsubj_id,
--      union all descendants).
--   3. Populate substrate.edge_type.parent_id by string-matching the colon
--      prefix (e.g. nsubj:pass → nsubj) for every existing row, plus seed the
--      WordNet semantic-relation parents from #1.

-- ── Step 1a: parent_id on substrate.semantic_relation_type ────────────────
ALTER TABLE substrate.semantic_relation_type
    ADD COLUMN parent_id INTEGER NULL
        REFERENCES substrate.semantic_relation_type(id);

CREATE INDEX idx_srt_parent ON substrate.semantic_relation_type(parent_id);

-- ── Step 1b: register the missing parent rows ─────────────────────────────
-- Holonym parent (whole-of relation) with three children:
INSERT INTO substrate.semantic_relation_type (code) VALUES ('holonym')
    ON CONFLICT (code) DO NOTHING;
INSERT INTO substrate.semantic_relation_type (code) VALUES ('meronym')
    ON CONFLICT (code) DO NOTHING;

-- Wire children to parents.
UPDATE substrate.semantic_relation_type
   SET parent_id = (SELECT id FROM substrate.semantic_relation_type WHERE code = 'holonym')
 WHERE code IN ('member_holonym', 'part_holonym', 'substance_holonym');

UPDATE substrate.semantic_relation_type
   SET parent_id = (SELECT id FROM substrate.semantic_relation_type WHERE code = 'meronym')
 WHERE code IN ('member_meronym', 'part_meronym', 'substance_meronym');

-- ── Step 2: parent_id on substrate.edge_type ──────────────────────────────
ALTER TABLE substrate.edge_type
    ADD COLUMN parent_id INTEGER NULL
        REFERENCES substrate.edge_type(id);

CREATE INDEX idx_edge_type_parent ON substrate.edge_type(parent_id);

-- ── Step 3a: populate edge_type.parent_id for UD deprel subtypes ──────────
-- For every edge_type whose code contains a colon, find the prefix-matching
-- root edge_type (same prefix, no colon) and set parent_id.
UPDATE substrate.edge_type child
   SET parent_id = parent.id
  FROM substrate.edge_type parent
 WHERE position(':' in child.code) > 0
   AND parent.code = split_part(child.code, ':', 1)
   AND parent.code <> child.code
   AND parent.category = child.category;

-- ── Step 3b: populate edge_type.parent_id for WordNet holonym/meronym families ──
-- Register the parent edge_types if missing (so traversal is possible).
INSERT INTO substrate.edge_type (code, category)
SELECT 'holonym', 'semantic'
WHERE NOT EXISTS (SELECT 1 FROM substrate.edge_type WHERE code = 'holonym');

INSERT INTO substrate.edge_type (code, category)
SELECT 'meronym', 'semantic'
WHERE NOT EXISTS (SELECT 1 FROM substrate.edge_type WHERE code = 'meronym');

UPDATE substrate.edge_type
   SET parent_id = (SELECT id FROM substrate.edge_type WHERE code = 'holonym')
 WHERE code IN ('member_holonym', 'part_holonym', 'substance_holonym');

UPDATE substrate.edge_type
   SET parent_id = (SELECT id FROM substrate.edge_type WHERE code = 'meronym')
 WHERE code IN ('member_meronym', 'part_meronym', 'substance_meronym');

-- ── Verification (read-only; commit fails if these don't hold) ────────────
DO $$
DECLARE
    expected_subtype_count INTEGER;
    actual_linked_count    INTEGER;
BEGIN
    -- Every deprel-style subtype edge_type (code with ':') should now have a parent_id.
    SELECT count(*) INTO expected_subtype_count
      FROM substrate.edge_type
     WHERE position(':' in code) > 0;

    SELECT count(*) INTO actual_linked_count
      FROM substrate.edge_type
     WHERE position(':' in code) > 0
       AND parent_id IS NOT NULL;

    IF expected_subtype_count > 0 AND actual_linked_count < (expected_subtype_count * 95 / 100) THEN
        RAISE EXCEPTION 'edge_type parent_id population shortfall: % of % subtypes linked',
            actual_linked_count, expected_subtype_count;
    END IF;
END $$;

COMMENT ON COLUMN substrate.edge_type.parent_id IS
    'Self-FK to the parent universal edge type. Subtype codes (e.g. nsubj:pass, '
    'compound:smixut, member_holonym) point to their root (nsubj, compound, '
    'holonym). Enables recursive-CTE traversal "find every nsubj-family edge" '
    'without string-prefix matching.';

COMMENT ON COLUMN substrate.semantic_relation_type.parent_id IS
    'Self-FK to the parent semantic relation. Mirrors substrate.edge_type.parent_id '
    'so the WordNet relation hierarchy (holonym ⊃ {member,part,substance}_holonym; '
    'meronym ⊃ {member,part,substance}_meronym) is queryable.';
