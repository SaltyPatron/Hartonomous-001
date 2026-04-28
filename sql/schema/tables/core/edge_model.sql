-- Edge types 22..33: in_model, in_layer, has_dtype, has_shape, has_hidden_size,
-- has_num_layers, has_num_attention_heads, has_vocab_size, has_token_string,
-- has_token_id, in_vocabulary, co_occurrence. Plus 38..39: has_tensor, has_architecture_name.
CREATE TABLE substrate.edge_model
    PARTITION OF substrate.edge FOR VALUES IN (22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 38, 39);
