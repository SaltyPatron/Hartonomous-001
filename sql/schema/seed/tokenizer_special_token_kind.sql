INSERT INTO substrate.tokenizer_special_token_kind (id, code) VALUES
    -- Classical NLP / encoder-style
    ( 1, 'bos'),
    ( 2, 'eos'),
    ( 3, 'pad'),
    ( 4, 'unk'),
    ( 5, 'mask'),
    ( 6, 'sep'),
    ( 7, 'cls'),
    -- Chat role markers
    (10, 'role_system'),
    (11, 'role_user'),
    (12, 'role_assistant'),
    (13, 'role_tool'),
    -- Turn / header markers
    (20, 'turn_start'),     -- ChatML <|im_start|>, Phi <|user|>
    (21, 'turn_end'),       -- ChatML <|im_end|>, Llama-3 <|eot_id|>
    (22, 'header_start'),   -- Llama-3 <|start_header_id|>
    (23, 'header_end'),     -- Llama-3 <|end_header_id|>
    -- Tool calling
    (30, 'tool_call_begin'),
    (31, 'tool_call_end'),
    (32, 'tool_response_begin'),
    (33, 'tool_response_end'),
    (34, 'python_tag'),     -- Llama-3 <|python_tag|>
    (35, 'eom'),            -- Llama-3 <|eom_id|>
    -- Reasoning / chain-of-thought
    (40, 'reasoning_begin'),    -- <think> / <|reasoning|>
    (41, 'reasoning_end'),      -- </think> / <|/reasoning|>
    (42, 'channel_analysis'),
    (43, 'channel_final'),
    -- Fill-in-middle
    (50, 'fim_prefix'),
    (51, 'fim_middle'),
    (52, 'fim_suffix'),
    -- Catch-all
    (255, 'reserved');
