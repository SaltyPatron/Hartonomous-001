-- Partial unique index — vocab_id is unique within a tokenizer only for
-- ASSIGNED ids. The sentinel -1 means "per-model varies"; multiple roles
-- can carry -1 on the same family hash until a model adopts them.
CREATE UNIQUE INDEX tokenizer_special_token_assigned_vocab_idx
    ON substrate.tokenizer_special_token (tokenizer_hash, vocab_id)
    WHERE vocab_id >= 0;
