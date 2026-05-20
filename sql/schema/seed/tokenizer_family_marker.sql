-- Per-family marker bytes. Marker bytes are literal UTF-8 of the
-- corresponding character(s) per published family convention:
--   wordpiece continuation_prefix  → '##' = 0x23 0x23
--   sentencepiece metaspace        → '▁' (U+2581) = 0xE2 0x96 0x81
--   byte_level_bpe byte_encoded_space → 'Ġ' (U+0120) = 0xC4 0xA0
--   byte_level_bpe byte_encoded_newline → 'Ċ' (U+010A) = 0xC4 0x8A
--   moses_bpe continuation_suffix  → '@@' = 0x40 0x40
INSERT INTO substrate.tokenizer_family_marker (family_id, role_id, marker_bytes) VALUES
    (1, 2, E'\\x2323'),       -- wordpiece + continuation_prefix
    (2, 6, E'\\xE29681'),     -- sentencepiece_unigram + metaspace
    (3, 6, E'\\xE29681'),     -- sentencepiece_bpe + metaspace
    (4, 4, E'\\xC4A0'),       -- byte_level_bpe + byte_encoded_space
    (4, 5, E'\\xC48A'),       -- byte_level_bpe + byte_encoded_newline
    (5, 4, E'\\xC4A0'),       -- tiktoken + byte_encoded_space
    (5, 5, E'\\xC48A'),       -- tiktoken + byte_encoded_newline
    (7, 3, E'\\x4040');       -- moses_bpe + continuation_suffix
