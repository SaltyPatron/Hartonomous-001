-- App-tier / substrate-tier: per-tokenizer special token registry. Each
-- row links a tokenizer (either a synthetic family-tier hash like
-- BLAKE3('tokenizer-family:llama-3') or a per-ingested-model
-- tokenizer_model entity hash) to a literal token string + its semantic
-- kind + the vocab_id it occupies in that tokenizer's integer space.
--
-- Three-tier population:
--   * App: synthetic family-tier rows seeded at db-bootstrap (Llama-3
--     specials, ChatML specials, INST-block specials, GPT-2 specials,
--     etc.) with vocab_id where conventional, -1 where per-model varies.
--   * Substrate: SafetensorsDecomposer end-pass populates per-ingested-
--     model rows with the model's actual vocab_id assignments.
--   * User: practitioner-forked custom special tokens.
--
-- PK is (tokenizer_hash, kind_id, token_string) because vocab_id = -1 is
-- a sentinel for "per-model varies" and the same family can carry many
-- -1 rows (different roles each pinned to -1 until a model adopts them).
-- (tokenizer_hash, vocab_id) is unique only for ASSIGNED ids; enforced
-- by a partial unique index below.
CREATE TABLE substrate.tokenizer_special_token (
    tokenizer_hash  substrate.hash_value NOT NULL,
    kind_id         SMALLINT             NOT NULL REFERENCES substrate.tokenizer_special_token_kind(id),
    token_string    TEXT                 NOT NULL,
    vocab_id        INT                  NOT NULL,
    PRIMARY KEY (tokenizer_hash, kind_id, token_string)
);

COMMENT ON TABLE substrate.tokenizer_special_token IS
    'Per-(tokenizer, special_token) registry. Hot lookup during encode (apply chat template → emit kind=turn_start → resolve to family-specific token string → lookup vocab_id) and decode (vocab_id → kind → suppress from user-visible output or route to reasoning channel). App-tier family rows + substrate-tier per-ingested-model rows share the table; the tokenizer_hash distinguishes them. PK is (tokenizer_hash, kind_id, token_string); vocab_id unique enforced only for assigned (>=0) ids via partial index.';
