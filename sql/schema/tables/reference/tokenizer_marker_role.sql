-- App-tier reference: roles a tokenizer marker can play in its family's
-- pipeline. Examples: 'leading_space' (Llama BPE prefixes word-initial
-- tokens with a literal space), 'continuation_prefix' (WordPiece '##'),
-- 'byte_encoded_space' (GPT-2 'Ġ' U+0120), 'metaspace' (SentencePiece
-- '▁' U+2581).
CREATE TABLE substrate.tokenizer_marker_role (
    id   SMALLINT PRIMARY KEY,
    code TEXT     NOT NULL UNIQUE
);

COMMENT ON TABLE substrate.tokenizer_marker_role IS
    'Bounded reference catalog of tokenizer marker roles (leading_space, continuation_prefix, continuation_suffix, byte_encoded_space, byte_encoded_newline, metaspace). App-tier static enum.';
