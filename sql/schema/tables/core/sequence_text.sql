-- text_composition (6), paragraph (7), document (8).
-- Most sequence rows in the substrate land here — every text decomposition
-- emits document → paragraphs → text_compositions chains. bpe_token was
-- removed; tokenizer outputs are word_forms (which seq under entity_word=3).
CREATE TABLE substrate.sequence_text
    PARTITION OF substrate.sequence FOR VALUES IN (6, 7, 8);
