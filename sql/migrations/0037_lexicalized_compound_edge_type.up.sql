-- 0037_lexicalized_compound_edge_type.up.sql
--
-- Adds the `lexicalized_compound` structural edge type.
--
-- Rationale (semantic regression #2 — "highrise"):
--   Multi-word and underscore-joined lemmas like "high_rise", "ice_cream",
--   "open up", "rock 'n' roll" are simultaneously a single conceptual
--   surface form AND a composition of word_forms. The substrate must
--   record BOTH paths so inference can traverse either:
--     * whole-form path: lemma "high_rise" as one Merkle entity,
--       carrying its own senses and inflections.
--     * parts-composition path: word_form "high" + word_form "rise"
--       as separate Merkle entities, each available for individual
--       lookup and convergence with monomorphemic occurrences elsewhere.
--   The lexicalized_compound edge connects whole↔parts in role-ordered
--   form, with the whole as `source` and each part as `target` carrying
--   its left-to-right ordinal as the edge_member position.
--
-- Edge category is `structural` → routes into the existing
-- substrate.edge_structural partition (no schema change required).

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('lexicalized_compound', 'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'lemma'),
        (SELECT id FROM substrate.entity_type WHERE code = 'word_form'));
