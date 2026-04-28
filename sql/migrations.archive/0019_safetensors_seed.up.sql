-- 0019_safetensors_seed.up.sql
-- Physicality types, tensor roles, architecture classes, and edge types for M5e safetensors decomposer.

-- New physicality type for Track 1 embedding fireflies (4D POINTZM in shared concept-space frame).
INSERT INTO substrate.physicality_type (code) VALUES
    ('embedding_firefly')
ON CONFLICT (code) DO NOTHING;

-- Tensor role vocabulary — covers the Consolidated Tensor Role Coverage table in safetensors.md.
-- Track 1 roles (wholesale, become fireflies):
INSERT INTO substrate.tensor_role (code) VALUES
    ('token_embedding'),
    ('token_type_embedding'),
    ('position_embedding'),
    ('position_embedding_2d'),
    ('rope_freq'),
    ('vq_codebook'),
    ('object_query'),
    ('anchor_grid'),
    -- Track 2 roles (transformation weights):
    ('attention_query'),
    ('attention_key'),
    ('attention_value'),
    ('attention_output'),
    ('cross_attention'),
    ('ffn_gate'),
    ('ffn_up'),
    ('ffn_down'),
    ('moe_router'),
    ('moe_expert_gate'),
    ('moe_expert_up'),
    ('moe_expert_down'),
    ('moe_shared_expert'),
    ('layer_norm'),
    ('batch_norm'),
    ('rms_norm'),
    ('logit_head'),
    ('class_head'),
    ('bbox_head'),
    ('conv_kernel'),
    ('diffusion_block'),
    ('vae_block'),
    ('conformer_layer'),
    ('mel_filterbank'),
    ('audio_codec_encoder'),
    ('audio_codec_decoder'),
    ('vision_feature'),
    ('vision_projection'),
    ('modality_projection'),
    ('lora_a'),
    ('lora_b'),
    ('codebook_scale'),
    ('fp8_scale')
ON CONFLICT (code) DO NOTHING;

-- Architecture classes — models currently targeted in D:\Models\hub.
INSERT INTO substrate.architecture_class (code) VALUES
    ('BertModel'),
    ('BertForMaskedLM'),
    ('BertForSequenceClassification'),
    ('Qwen2ForCausalLM'),
    ('Qwen3ForCausalLM'),
    ('Qwen3MoeForCausalLM'),
    ('DeepseekV3ForCausalLM'),
    ('Llama4ForConditionalGeneration'),
    ('DetrForObjectDetection'),
    ('ConditionalDetrForObjectDetection'),
    ('RTDetrForObjectDetection'),
    ('GroundingDinoForObjectDetection'),
    ('Florence2ForConditionalGeneration'),
    ('MusicFlamingoModel'),
    ('GraniteSpeechForConditionalGeneration'),
    ('FishSpeechForCausalLM'),
    ('SAMAudioLargeModel'),
    ('FluxTransformer2DModel'),
    ('CanaryModel'),
    ('YOLOModel'),
    ('CLIPModel'),
    ('T5ForConditionalGeneration')
ON CONFLICT (code) DO NOTHING;

-- Edge types for Track 2 functional-sparsity-filtered transformation patterns.
-- Structural edges describe model anatomy; model_derived edges are Track 2 functional patterns.
INSERT INTO substrate.edge_type (code, category) VALUES
    ('has_tensor', 'structural'),
    ('has_dtype', 'structural'),
    ('has_shape', 'structural'),
    ('has_hidden_size', 'structural'),
    ('has_num_layers', 'structural'),
    ('has_num_attention_heads', 'structural'),
    ('has_vocab_size', 'structural'),
    ('has_intermediate_size', 'structural'),
    ('has_max_position_embeddings', 'structural'),
    ('has_token_id', 'structural'),
    ('has_token_string', 'structural'),
    ('in_vocabulary', 'structural'),
    ('encodes_attention_archetype', 'model_derived'),
    ('encodes_attention_output', 'model_derived'),
    ('encodes_cross_attention', 'model_derived'),
    ('encodes_ffn_gate', 'model_derived'),
    ('encodes_ffn_neuron', 'model_derived'),
    ('encodes_moe_route', 'model_derived'),
    ('encodes_moe_expert', 'model_derived'),
    ('encodes_moe_shared', 'model_derived'),
    ('encodes_logit_projection', 'model_derived'),
    ('encodes_class_prediction', 'model_derived'),
    ('encodes_bbox_prediction', 'model_derived'),
    ('encodes_conv_filter', 'model_derived'),
    ('encodes_diffusion_transformer_block', 'model_derived'),
    ('encodes_vae_block', 'model_derived'),
    ('encodes_conformer_block', 'model_derived'),
    ('encodes_audio_codec_enc', 'model_derived'),
    ('encodes_audio_codec_dec', 'model_derived'),
    ('encodes_vision_feature', 'model_derived'),
    ('encodes_vision_projection', 'model_derived'),
    ('encodes_modality_projection', 'model_derived'),
    ('encodes_lora_adapter', 'model_derived')
ON CONFLICT (code) DO NOTHING;

-- Provenance rows for model publishers. Individual models add their snapshot-hash-qualified
-- provenance dynamically at decompose time; these are the high-level trust-prior tiers.
INSERT INTO substrate.provenance (code, curator_class, initial_mu) VALUES
    ('huggingface_sentence_transformers', 'publisher', 80000.0),
    ('huggingface_meta', 'publisher', 85000.0),
    ('huggingface_google', 'publisher', 85000.0),
    ('huggingface_microsoft', 'publisher', 85000.0),
    ('huggingface_qwen', 'publisher', 82000.0),
    ('huggingface_deepseek', 'publisher', 82000.0),
    ('huggingface_nvidia', 'publisher', 85000.0),
    ('huggingface_black_forest_labs', 'publisher', 78000.0),
    ('huggingface_ultralytics', 'publisher', 75000.0),
    ('huggingface_community', 'publisher', 60000.0)
ON CONFLICT (code) DO NOTHING;
