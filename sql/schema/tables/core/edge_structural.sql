-- Edge types 1..13: has_sense, has_form, has_lemma, has_morpheme, has_gloss,
-- has_example, has_name, has_text, inflection_of, has_etymology,
-- has_pronunciation, has_hyphenation, has_wikidata. Plus 37 lexicalized_compound.
CREATE TABLE substrate.edge_structural
    PARTITION OF substrate.edge FOR VALUES IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 37);
