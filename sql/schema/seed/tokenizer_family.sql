INSERT INTO substrate.tokenizer_family (id, code, pre_tokenizer_kind, normalizer_kind, decoder_kind) VALUES
    (1, 'wordpiece',              'whitespace_split', 'nfc',  'wordpiece'),
    (2, 'sentencepiece_unigram',  'metaspace',        'nfkc', 'metaspace'),
    (3, 'sentencepiece_bpe',      'metaspace',        'nfkc', 'metaspace'),
    (4, 'byte_level_bpe',         'byte_level',       'none', 'byte_level'),
    (5, 'tiktoken',               'byte_level',       'none', 'byte_level'),
    (6, 'bpe_classical',          'whitespace_split', 'nfc',  'bpe'),
    (7, 'moses_bpe',              'whitespace_split', 'nfc',  'bpe');
