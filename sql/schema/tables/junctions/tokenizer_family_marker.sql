-- App-tier junction: per-family marker assignment. Maps each family to
-- the literal UTF-8 bytes it uses for each role (NULL = family doesn't
-- use that role; e.g. byte_level_bpe doesn't have metaspace).
CREATE TABLE substrate.tokenizer_family_marker (
    family_id     SMALLINT NOT NULL REFERENCES substrate.tokenizer_family(id),
    role_id       SMALLINT NOT NULL REFERENCES substrate.tokenizer_marker_role(id),
    marker_bytes  BYTEA    NOT NULL,
    PRIMARY KEY (family_id, role_id)
);

COMMENT ON TABLE substrate.tokenizer_family_marker IS
    'Per-(family, role) literal UTF-8 marker bytes. E.g. WordPiece continuation_prefix = 0x2323 (##); SentencePiece metaspace = 0xE29681 (▁); byte_level_bpe byte_encoded_space = 0xC4A0 (Ġ). Used by the encoder when inserting markers per family convention and by the decoder when stripping them.';
