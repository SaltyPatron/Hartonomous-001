-- Synthetic family-tier tokenizer_hash values (BLAKE3 of the literal family
-- name UTF-8 bytes). Per-ingested-model rows reuse the same table with the
-- actual tokenizer_model entity_hash; the schema doesn't distinguish at
-- storage time. vocab_id = -1 means "varies by model" (used for ChatML /
-- INST families that are reused across multiple model implementations,
-- each assigning their own integer ID). vocab_id > 0 means "this family
-- has a canonical ID" (Llama-3's 128000..128255 reserved range, etc.).

-- ── Llama-3 family (and Llama-3.1 / 3.2 / 3.3 / 4 inheritors) ──────────
WITH llama3 AS (
    SELECT blake3_hash(convert_to('tokenizer-family:llama-3', 'UTF8'))::substrate.hash_value AS h
)
INSERT INTO substrate.tokenizer_special_token (tokenizer_hash, kind_id, token_string, vocab_id)
SELECT h, kind_id, token_string, vocab_id FROM llama3, (VALUES
    ( 1, '<|begin_of_text|>',    128000),
    ( 2, '<|end_of_text|>',      128001),
    (22, '<|start_header_id|>',  128006),
    (23, '<|end_header_id|>',    128007),
    (35, '<|eom_id|>',           128008),
    (21, '<|eot_id|>',           128009),
    (34, '<|python_tag|>',       128255)
) AS s(kind_id, token_string, vocab_id);

-- ── ChatML family (Qwen-2/2.5/3, DeepSeek-V2/V3, Yi, OpenChat, many
--                  community fine-tunes; vocab_ids are per-model) ─────
WITH chatml AS (
    SELECT blake3_hash(convert_to('tokenizer-family:chatml', 'UTF8'))::substrate.hash_value AS h
)
INSERT INTO substrate.tokenizer_special_token (tokenizer_hash, kind_id, token_string, vocab_id)
SELECT h, kind_id, token_string, vocab_id FROM chatml, (VALUES
    (20, '<|im_start|>',     -1),
    (21, '<|im_end|>',       -1),
    (30, '<tool_call>',      -1),
    (31, '</tool_call>',     -1),
    (32, '<tool_response>',  -1),
    (33, '</tool_response>', -1),
    (40, '<think>',          -1),
    (41, '</think>',         -1)
) AS s(kind_id, token_string, vocab_id);

-- ── Mistral / Llama-2 INST-block family ──────────────────────────────
WITH inst AS (
    SELECT blake3_hash(convert_to('tokenizer-family:inst-block', 'UTF8'))::substrate.hash_value AS h
)
INSERT INTO substrate.tokenizer_special_token (tokenizer_hash, kind_id, token_string, vocab_id)
SELECT h, kind_id, token_string, vocab_id FROM inst, (VALUES
    ( 1, '<s>',                1),
    ( 2, '</s>',               2),
    (20, '[INST]',            -1),
    (21, '[/INST]',           -1),
    (30, '[TOOL_CALLS]',      -1),
    (31, '[/TOOL_CALLS]',     -1),
    (32, '[TOOL_RESULTS]',    -1),
    (33, '[/TOOL_RESULTS]',   -1)
) AS s(kind_id, token_string, vocab_id);

-- ── Gemma family ─────────────────────────────────────────────────────
WITH gemma AS (
    SELECT blake3_hash(convert_to('tokenizer-family:gemma', 'UTF8'))::substrate.hash_value AS h
)
INSERT INTO substrate.tokenizer_special_token (tokenizer_hash, kind_id, token_string, vocab_id)
SELECT h, kind_id, token_string, vocab_id FROM gemma, (VALUES
    ( 1, '<bos>',                  2),
    ( 2, '<eos>',                  1),
    ( 3, '<pad>',                  0),
    (20, '<start_of_turn>',      105),
    (21, '<end_of_turn>',        106)
) AS s(kind_id, token_string, vocab_id);

-- ── Phi family ───────────────────────────────────────────────────────
WITH phi AS (
    SELECT blake3_hash(convert_to('tokenizer-family:phi', 'UTF8'))::substrate.hash_value AS h
)
INSERT INTO substrate.tokenizer_special_token (tokenizer_hash, kind_id, token_string, vocab_id)
SELECT h, kind_id, token_string, vocab_id FROM phi, (VALUES
    (10, '<|system|>',     -1),
    (11, '<|user|>',       -1),
    (12, '<|assistant|>',  -1),
    (21, '<|end|>',        -1),
    ( 2, '<|endoftext|>',  -1)
) AS s(kind_id, token_string, vocab_id);

-- ── DeepSeek-V3 family ────────────────────────────────────────────────
WITH ds AS (
    SELECT blake3_hash(convert_to('tokenizer-family:deepseek-v3', 'UTF8'))::substrate.hash_value AS h
)
INSERT INTO substrate.tokenizer_special_token (tokenizer_hash, kind_id, token_string, vocab_id)
SELECT h, kind_id, token_string, vocab_id FROM ds, (VALUES
    (30, '<|tool_calls_begin|>',    -1),
    (30, '<|tool_call_begin|>',     -1),
    (31, '<|tool_call_end|>',       -1),
    (31, '<|tool_calls_end|>',      -1),
    (32, '<|tool_outputs_begin|>',  -1),
    (33, '<|tool_outputs_end|>',    -1),
    (40, '<think>',                 -1),
    (41, '</think>',                -1)
) AS s(kind_id, token_string, vocab_id);

-- ── GPT-2 family (and GPT-Neo / RoBERTa / byte-level-BPE inheritors) ─
WITH gpt2 AS (
    SELECT blake3_hash(convert_to('tokenizer-family:gpt-2', 'UTF8'))::substrate.hash_value AS h
)
INSERT INTO substrate.tokenizer_special_token (tokenizer_hash, kind_id, token_string, vocab_id)
SELECT h, kind_id, token_string, vocab_id FROM gpt2, (VALUES
    ( 2, '<|endoftext|>',  50256)
) AS s(kind_id, token_string, vocab_id);

-- ── BERT family (WordPiece) ──────────────────────────────────────────
WITH bert AS (
    SELECT blake3_hash(convert_to('tokenizer-family:bert', 'UTF8'))::substrate.hash_value AS h
)
INSERT INTO substrate.tokenizer_special_token (tokenizer_hash, kind_id, token_string, vocab_id)
SELECT h, kind_id, token_string, vocab_id FROM bert, (VALUES
    ( 3, '[PAD]',  0),
    ( 4, '[UNK]', 100),
    ( 7, '[CLS]', 101),
    ( 6, '[SEP]', 102),
    ( 5, '[MASK]', 103)
) AS s(kind_id, token_string, vocab_id);
