-- Entity types 9..12: text_composition, paragraph, document, bpe_token.
-- Co-located in one partition because they share access patterns (text
-- decomposition output) and are produced by the same set of decomposers.
CREATE TABLE substrate.entity_text
    PARTITION OF substrate.entity FOR VALUES IN (9, 10, 11, 12);
