-- App-tier reference: tokenizer-family enum + per-family pipeline kinds.
-- Bounded cardinality (~10 families), single-source authoritative (each
-- family's conventions are published by its authors), microsecond JOIN
-- target. Hot lookups during encode/decode go through the libhartonomous
-- perf-cache blob; this table is the substrate-queryable backing.
CREATE TABLE substrate.tokenizer_family (
    id                  SMALLINT PRIMARY KEY,
    code                TEXT     NOT NULL UNIQUE,
    -- 'wordpiece' | 'sentencepiece_unigram' | 'sentencepiece_bpe'
    -- | 'byte_level_bpe' | 'tiktoken' | 'bpe_classical' | 'moses_bpe'
    pre_tokenizer_kind  TEXT     NOT NULL,
    -- 'whitespace_split' | 'byte_level' | 'metaspace' | 'punctuation' | 'no_split'
    normalizer_kind     TEXT     NOT NULL,
    -- 'nfc' | 'nfd' | 'nfkc' | 'nfkd' | 'lowercase' | 'strip_accents' | 'none'
    decoder_kind        TEXT     NOT NULL
    -- 'wordpiece' | 'bpe' | 'metaspace' | 'byte_level' | 'sequence'
);

COMMENT ON TABLE substrate.tokenizer_family IS
    'Bounded reference catalog of tokenizer families with their pipeline-component kinds (normalizer / pre_tokenizer / decoder). App-tier static data; perf-cache blob mirrors this in C arrays for microsecond startup-time lookup during encode/decode.';
