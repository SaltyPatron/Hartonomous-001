-- 0019_safetensors_seed.down.sql
-- Revert safetensors seed (physicality_type, tensor_role, architecture_class, edge_type, provenance rows).

DELETE FROM substrate.provenance WHERE code IN (
    'huggingface_sentence_transformers','huggingface_meta','huggingface_google',
    'huggingface_microsoft','huggingface_qwen','huggingface_deepseek',
    'huggingface_nvidia','huggingface_black_forest_labs','huggingface_ultralytics',
    'huggingface_community'
);

DELETE FROM substrate.edge_type WHERE code IN (
    'has_tensor','has_dtype','has_shape','has_hidden_size','has_num_layers',
    'has_num_attention_heads','has_vocab_size','has_intermediate_size',
    'has_max_position_embeddings','has_token_id','has_token_string','in_vocabulary',
    'encodes_attention_archetype','encodes_attention_output','encodes_cross_attention',
    'encodes_ffn_gate','encodes_ffn_neuron','encodes_moe_route','encodes_moe_expert',
    'encodes_moe_shared','encodes_logit_projection','encodes_class_prediction',
    'encodes_bbox_prediction','encodes_conv_filter','encodes_diffusion_transformer_block',
    'encodes_vae_block','encodes_conformer_block','encodes_audio_codec_enc',
    'encodes_audio_codec_dec','encodes_vision_feature','encodes_vision_projection',
    'encodes_modality_projection','encodes_lora_adapter'
);

DELETE FROM substrate.architecture_class WHERE code IN (
    'BertModel','BertForMaskedLM','BertForSequenceClassification',
    'Qwen2ForCausalLM','Qwen3ForCausalLM','Qwen3MoeForCausalLM','DeepseekV3ForCausalLM',
    'Llama4ForConditionalGeneration','DetrForObjectDetection',
    'ConditionalDetrForObjectDetection','RTDetrForObjectDetection',
    'GroundingDinoForObjectDetection','Florence2ForConditionalGeneration',
    'MusicFlamingoModel','GraniteSpeechForConditionalGeneration','FishSpeechForCausalLM',
    'SAMAudioLargeModel','FluxTransformer2DModel','CanaryModel','YOLOModel',
    'CLIPModel','T5ForConditionalGeneration'
);

DELETE FROM substrate.tensor_role WHERE code IN (
    'token_embedding','token_type_embedding','position_embedding','position_embedding_2d',
    'rope_freq','vq_codebook','object_query','anchor_grid',
    'attention_query','attention_key','attention_value','attention_output','cross_attention',
    'ffn_gate','ffn_up','ffn_down','moe_router','moe_expert_gate','moe_expert_up',
    'moe_expert_down','moe_shared_expert','layer_norm','batch_norm','rms_norm',
    'logit_head','class_head','bbox_head','conv_kernel','diffusion_block','vae_block',
    'conformer_layer','mel_filterbank','audio_codec_encoder','audio_codec_decoder',
    'vision_feature','vision_projection','modality_projection','lora_a','lora_b',
    'codebook_scale','fp8_scale'
);

DELETE FROM substrate.physicality_type WHERE code = 'embedding_firefly';
