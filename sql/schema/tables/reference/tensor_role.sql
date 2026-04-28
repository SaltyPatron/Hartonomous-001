CREATE TABLE substrate.tensor_role (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.tensor_role IS
    'Tensor classification: attention_q, attention_k, attention_v, attention_o, ffn_up, ffn_down, ffn_gate, embed, lm_head, layer_norm_pre, layer_norm_post, rope_freq, moe_router, moe_expert, etc.';
