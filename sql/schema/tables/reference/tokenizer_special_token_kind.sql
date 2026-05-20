-- App-tier reference: semantic role classes for tokenizer special tokens.
-- A given token string like '<|im_start|>' has kind 'turn_start';
-- '<|tool_calls_begin|>' has kind 'tool_call_begin'; '<think>' has kind
-- 'reasoning_begin'; '<|begin_of_text|>' has kind 'bos'. The kind enum
-- lets the encoder/decoder reason about special tokens semantically
-- regardless of which family they come from.
CREATE TABLE substrate.tokenizer_special_token_kind (
    id   SMALLINT PRIMARY KEY,
    code TEXT     NOT NULL UNIQUE
);

COMMENT ON TABLE substrate.tokenizer_special_token_kind IS
    'Bounded reference enum of tokenizer special-token semantic roles (bos/eos/pad/unk/mask/sep/cls/role_*/turn_*/header_*/tool_call_*/tool_response_*/reasoning_*/channel_*/python_tag/eom/fim_*/reserved). Cross-family normalization of token meaning; ChatML <|im_start|> and Llama-3 <|start_header_id|> both classify as kind="turn_start".';
