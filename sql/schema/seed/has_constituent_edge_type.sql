-- has_constituent edge type — composition's ordered children.
--
-- Source role: parent composition entity (any text composition tier:
-- grapheme_cluster, word_form, lemma, ud_sentence, text_composition,
-- paragraph, document; or model compositions: tensor, model_architecture).
-- Target role: each child entity in left-to-right traversal order.
--
-- One n-ary edge per composition with the parent in role 'source' and each
-- ordered child in role 'target'. Position field on edge_member preserves
-- traversal order. Mirrors the lexicalized_compound shape.
--
-- This edge is the substrate's only authoritative parent → children
-- traversal record. Without it, recompose_text and any structural walk
-- can't recover compositional ordering from substrate.entity alone.
INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_constituent', 'structural', NULL, NULL)
ON CONFLICT (code) DO NOTHING;
