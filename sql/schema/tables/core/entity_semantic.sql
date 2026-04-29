-- Entity type 9: synset.
-- Lexical-semantic layer atop word_form/lemma. inflected_form was removed —
-- inflection is the relationship (inflection_of edge: word_form → lemma),
-- not the entity. word_sense and wikt_sense were also removed — sense is
-- captured by the lemma → synset has_sense edge with provenance distinguishing
-- the source dictionary.
CREATE TABLE substrate.entity_semantic
    PARTITION OF substrate.entity FOR VALUES IN (9);
