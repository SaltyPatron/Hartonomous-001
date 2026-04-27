-- 0060_remaining_per_role_units.down.sql

DELETE FROM substrate.edge_type WHERE code IN (
    'has_rope_freqs',
    'has_object_query',
    'has_class_projection',
    'has_bbox_projection',
    'has_vision_feature',
    'has_modality_basis',
    'has_lora_component',
    'has_conv_filter',
    'has_diffusion_component',
    'has_conformer_component',
    'has_codec_filter'
);

DELETE FROM substrate.entity_type WHERE code IN (
    'rope_freq_table',
    'object_query_slot',
    'class_projection',
    'bbox_projection',
    'vision_feature_direction',
    'modality_basis_vector',
    'lora_component',
    'conv_filter',
    'diffusion_component',
    'conformer_component',
    'audio_codec_filter'
);
