-- text_composition (9), paragraph (10), document (11), bpe_token (12).
-- Most sequence rows in the substrate land here — every text decomposition
-- emits document → paragraphs → text_compositions chains.
CREATE TABLE substrate.sequence_text
    PARTITION OF substrate.sequence FOR VALUES IN (9, 10, 11, 12);
