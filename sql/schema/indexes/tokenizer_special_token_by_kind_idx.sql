-- Per-tokenizer + per-kind lookup. Hot during encode (chat template emits
-- kind=turn_start → resolve to family-specific token string + vocab_id)
-- and decode (vocab_id → kind → suppress or route).
CREATE INDEX tokenizer_special_token_by_kind_idx
    ON substrate.tokenizer_special_token (tokenizer_hash, kind_id);
