-- Reverse 0023: drop the per-role-unit entity_types and edge_types added by
-- the up migration. Will fail if any dependent rows have been inserted into
-- substrate.entity / substrate.edge for these types — drop those first.

DELETE FROM substrate.edge_type WHERE code IN (
    'has_attention_component',
    'has_codec_filter',
    'has_bbox_projection',
    'has_class_projection',
    'has_conformer_component',
    'has_conv_filter',
    'has_diffusion_component',
    'has_lora_component',
    'has_modality_basis',
    'has_moe_neuron',
    'has_route_direction',
    'has_object_query',
    'has_vision_feature'
);

DELETE FROM substrate.entity_type WHERE code IN (
    'audio_codec_filter',
    'bbox_projection',
    'class_projection',
    'conformer_component',
    'conv_filter',
    'diffusion_component',
    'lora_component',
    'modality_basis_vector',
    'moe_expert_neuron',
    'moe_route_direction',
    'object_query_slot',
    'vision_feature_direction'
);
