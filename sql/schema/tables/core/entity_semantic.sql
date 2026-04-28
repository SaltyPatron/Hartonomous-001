-- Entity types 13..16: synset, word_sense, wikt_sense, inflected_form.
-- Co-located because they form the lexical-semantic layer atop word_form/lemma.
CREATE TABLE substrate.entity_semantic
    PARTITION OF substrate.entity FOR VALUES IN (13, 14, 15, 16);
