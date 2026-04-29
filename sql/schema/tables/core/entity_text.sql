-- Entity types 6..8: text_composition, paragraph, document.
-- Co-located in one partition because they share access patterns (text
-- decomposition output) and are produced by the same set of decomposers.
-- bpe_token was removed — BPE tokens are word_forms (content-addressed
-- by their UTF-8 bytes); tokenizer associations are edges, not a type.
CREATE TABLE substrate.entity_text
    PARTITION OF substrate.entity FOR VALUES IN (6, 7, 8);
